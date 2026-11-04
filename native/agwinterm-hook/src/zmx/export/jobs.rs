//! A single-claim origin program supervised under the SSH PTY, never a ZMX workload.
use super::*;
use std::{io::BufRead, os::{fd::AsRawFd, unix::process::{CommandExt, ExitStatusExt}}, sync::atomic::{AtomicBool, Ordering}};

static STOP: AtomicBool = AtomicBool::new(false);
extern "C" fn stop(_: libc::c_int) { STOP.store(true, Ordering::Relaxed); }

fn line(reader: &mut impl BufRead) -> Result<Option<Value>> {
    let mut raw = Vec::new();
    reader.take(262145).read_until(b'\n', &mut raw)?;
    if raw.is_empty() { return Ok(None); }
    if raw.len() > 262144 || raw.last() != Some(&b'\n') { return Err("invalid job frame".into()); }
    Ok(Some(decode(&raw)?))
}

pub(super) fn run(root: &Path, config: &Value, job: &str) -> Result<Value> {
    let (session_id, nonce) = job.split_once('.').ok_or("invalid job ID")?;
    uuid(session_id)?;
    if nonce.len() != 32 || !nonce.bytes().all(|c| c.is_ascii_hexdigit()) { return Err("invalid job nonce".into()); }
    if unsafe { libc::isatty(0) } != 1 { return Err("job requires an SSH PTY".into()); }
    let inventory = tree(root, config)?;
    let session = inventory["remote"]["sessions"].as_array().ok_or("missing sessions")?.iter()
        .find(|row| row["id"] == session_id).ok_or("exact job session unavailable")?;
    let mut args = presentation_args(root, config, session)?;
    args[2] = "job".into();
    args.extend(["--job".into(), job.into()]);
    let mut relay = Command::new(text(config, "cli")?).args(args).env_remove("AGWINTERM_SESSION_ID")
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn()?;
    let result = supervise(root, config, &mut relay);
    let _ = relay.kill(); let _ = relay.wait();
    match result { Ok(code) => std::process::exit(code), Err(e) => Err(e) }
}

fn supervise(root: &Path, config: &Value, relay: &mut std::process::Child) -> Result<i32> {
    let mut writer = relay.stdin.take().ok_or("missing job writer")?;
    // A stuck Windows relay cannot block the supervisor's PTY hangup/cancellation loop.
    let fd = writer.as_raw_fd();
    unsafe {
        let flags = libc::fcntl(fd, libc::F_GETFL);
        if flags < 0 || libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK) < 0 { return Err(io::Error::last_os_error().into()); }
    }
    let mut reader = io::BufReader::new(relay.stdout.take().ok_or("missing job reader")?);
    let (send, receive) = std::sync::mpsc::sync_channel(16);
    std::thread::spawn(move || {
        while let Ok(Some(frame)) = line(&mut reader) { if send.send(frame).is_err() { break; } }
    });
    let frame = receive.recv_timeout(Duration::from_secs(25)).map_err(|_| "job claim unavailable")?;
    if frame["kind"] != "context" { return Err("missing job context".into()); }
    let context = &frame["context"];
    let command = string(context, "command")?;
    if command.is_empty() || command.len() > 65536 || command.contains('\0') { return Err("invalid origin command".into()); }
    let cwd = linux_path(text(context, "cwd")?)?;
    let environment = context["environment"].as_object().ok_or("invalid origin environment")?;
    let mut child = Command::new("/bin/sh");
    child.args(["-lc", command]).current_dir(cwd);
    for (key, value) in environment {
        let value = value.as_str().ok_or("invalid environment value")?;
        if key.is_empty() || key.contains(['=', '\0']) || value.contains('\0') { return Err("invalid environment entry".into()); }
        child.env(key, value);
    }
    // Child receives the existing SSH terminal; no pipe consumes its input or its resize signals.
    unsafe {
        for signal in [libc::SIGINT, libc::SIGQUIT, libc::SIGTSTP, libc::SIGTTOU, libc::SIGPIPE] { libc::signal(signal, libc::SIG_IGN); }
        libc::signal(libc::SIGHUP, stop as *const () as libc::sighandler_t);
        libc::signal(libc::SIGTERM, stop as *const () as libc::sighandler_t);
        child.pre_exec(|| {
            if libc::setpgid(0, 0) != 0 || libc::tcsetpgrp(0, libc::getpid()) != 0 { return Err(io::Error::last_os_error()); }
            for signal in [libc::SIGINT, libc::SIGQUIT, libc::SIGTSTP, libc::SIGTTOU, libc::SIGPIPE, libc::SIGHUP, libc::SIGTERM] {
                libc::signal(signal, libc::SIG_DFL);
            }
            Ok(())
        });
    }
    PRESENTING.store(true, Ordering::Relaxed);
    let mut program = match child.spawn() {
        Ok(child) => child,
        Err(_) => { let _ = writeln!(writer, "{{\"kind\":\"exit\",\"code\":127}}"); return Ok(127); }
    };
    let group = program.id() as i32;
    let mut cancelled = None;
    let mut status = None;
    let mut heartbeat = Instant::now();
    loop {
        while let Ok(frame) = receive.try_recv() { if frame["kind"] == "cancel" { STOP.store(true, Ordering::Relaxed); } }
        let mut poll = libc::pollfd { fd: 0, events: libc::POLLIN, revents: 0 };
        unsafe { libc::poll(&mut poll, 1, 0); }
        if poll.revents & (libc::POLLHUP | libc::POLLERR | libc::POLLNVAL) != 0 { STOP.store(true, Ordering::Relaxed); }
        if heartbeat.elapsed() >= Duration::from_secs(1) {
            // Origin UI loss leaves the claimed job on its PTY, matching agterm. Explicit endpoint revocation cancels it.
            let _ = writeln!(writer, "{{\"kind\":\"ping\"}}");
            if root.join("disabled").exists() || config_state(root).ok().as_ref() != Some(config) { STOP.store(true, Ordering::Relaxed); }
            heartbeat = Instant::now();
        }
        if STOP.load(Ordering::Relaxed) && cancelled.is_none() {
            cancelled = Some(Instant::now()); unsafe { libc::kill(-group, libc::SIGTERM); }
        }
        if status.is_none() { status = program.try_wait()?; }
        if let Some(since) = cancelled {
            if since.elapsed() >= Duration::from_secs(3) { unsafe { libc::kill(-group, libc::SIGKILL); } }
            if status.is_some() && unsafe { libc::kill(-group, 0) } != 0 { break; }
        } else if status.is_some() { break; }
        std::thread::sleep(Duration::from_millis(20));
    }
    unsafe { libc::tcsetpgrp(0, libc::getpgrp()); }
    let status = status.ok_or("missing job status")?;
    let code = status.code().unwrap_or_else(|| 128 + status.signal().unwrap_or(1));
    let _ = writeln!(writer, "{{\"kind\":\"exit\",\"code\":{code}}}");
    let _ = writer.flush();
    // Let the relay deliver the final outcome before its owned process is reaped.
    let until = Instant::now() + Duration::from_secs(5);
    while relay.try_wait()?.is_none() && Instant::now() < until { std::thread::sleep(Duration::from_millis(10)); }
    Ok(code)
}
