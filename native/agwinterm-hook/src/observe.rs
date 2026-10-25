//! WSL interop kills the Windows process on SIGINT without a ConsoleCancelKeyPress.
//! Own that signal on Linux, reap the interop child, and preserve observation outcomes.
use std::{
    io::{self, BufRead, BufReader, Read, Write},
    os::unix::process::CommandExt,
    process::{Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::Duration,
};

static INTERRUPTED: AtomicBool = AtomicBool::new(false);
unsafe extern "C" {
    fn signal(number: i32, handler: usize) -> usize;
}
extern "C" fn interrupt(_: i32) {
    INTERRUPTED.store(true, Ordering::SeqCst);
}

fn mode(args: &[String]) -> Option<bool> {
    // The CLI accepts transport/output options before or between command words.
    // Skip their values as well: a pipe named "events" is not a command.
    let mut words = Vec::new();
    let mut args_iter = args.iter().peekable();
    while let Some(arg) = args_iter.next() {
        if arg.starts_with("--") {
            if !matches!(arg.to_ascii_lowercase().as_str(), "--json" | "--watch")
                && args_iter
                    .peek()
                    .is_some_and(|value| !value.starts_with("--"))
            {
                args_iter.next();
            }
        } else {
            words.push(arg);
        }
    }
    let is = |i: usize, text: &str| words.get(i).is_some_and(|s| s.eq_ignore_ascii_case(text));
    if is(0, "session") && is(1, "wait") {
        Some(false)
    } else if is(0, "events") && args.iter().any(|s| s.eq_ignore_ascii_case("--watch")) {
        Some(true)
    } else {
        None
    }
}

fn completed_reply(bytes: &[u8], json: bool) -> Option<i32> {
    if json {
        let value: serde_json::Value = serde_json::from_slice(bytes).ok()?;
        match value["ok"].as_bool()? {
            false => Some(
                if value["code"] == "cancelled" && value["source"] == "client" {
                    130
                } else {
                    1
                },
            ),
            true => match value["result"]["outcome"].as_str()? {
                "matched" => Some(0),
                "timeout" => Some(3),
                _ => None,
            },
        }
    } else {
        match std::str::from_utf8(bytes).ok()?.trim() {
            "idle" | "active" | "blocked" | "completed" => Some(0),
            _ => None,
        }
    }
}

pub(super) fn run(args: Vec<String>) -> Result<i32, Box<dyn std::error::Error>> {
    let (cli, argv) = args
        .split_first()
        .ok_or("--ctl requires a Windows CLI path")?;
    let Some(watch) = mode(argv) else {
        return Err(Command::new(cli).args(argv).exec().into());
    };
    let json = argv.iter().any(|s| s.eq_ignore_ascii_case("--json"));
    // signal() is provided by both supported Linux C ABIs; the handler only stores an atomic bool.
    if unsafe { signal(2, interrupt as *const () as usize) } == usize::MAX {
        return Err(io::Error::last_os_error().into());
    }
    let mut child = Command::new(cli)
        .args(argv)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .spawn()?;
    let stdout = child.stdout.take().ok_or("missing CLI stdout")?;
    let (sender, receiver) = mpsc::sync_channel(1);
    let output = thread::spawn(move || {
        let result = (|| -> io::Result<Vec<u8>> {
            let mut reader = BufReader::new(stdout);
            if !watch {
                let mut bytes = Vec::new();
                reader.take(1024 * 1024 + 1).read_to_end(&mut bytes)?;
                if bytes.len() > 1024 * 1024 {
                    return Err(io::Error::other("wait reply exceeds 1 MiB"));
                }
                return Ok(bytes);
            }
            let mut out = io::stdout().lock();
            loop {
                let mut line = Vec::new();
                // At most 1000 records of 8 KiB plus page metadata; cap malformed responses too.
                let count = reader
                    .by_ref()
                    .take(16 * 1024 * 1024 + 1)
                    .read_until(b'\n', &mut line)?;
                if count == 0 {
                    break;
                }
                if line.len() > 16 * 1024 * 1024 || line.last() != Some(&b'\n') {
                    return Err(io::Error::other("incomplete or oversized watch page"));
                }
                out.write_all(&line)?;
                out.flush()?;
            }
            Ok(Vec::new())
        })();
        let _ = sender.send(result);
    });
    let mut captured = None;
    let status = loop {
        if INTERRUPTED.load(Ordering::SeqCst) {
            let _ = child.kill();
            break child.wait()?;
        }
        if let Some(status) = child.try_wait()? {
            break status;
        }
        match receiver.try_recv() {
            Ok(result) => {
                let failed = result.is_err();
                captured = Some(result);
                if failed {
                    let _ = child.kill();
                    break child.wait()?;
                }
            }
            Err(mpsc::TryRecvError::Disconnected) if captured.is_none() => {
                let _ = child.kill();
                break child.wait()?;
            }
            _ => {}
        }
        thread::sleep(Duration::from_millis(10));
    };
    if watch && INTERRUPTED.load(Ordering::SeqCst) {
        // Do not wait for a downstream consumer that stopped reading stdout. The child is
        // already reaped; main exits immediately and closes the forwarding thread's handles.
        eprintln!("observation cancelled");
        return Ok(130);
    }
    output
        .join()
        .map_err(|_| "observation output reader failed")?;
    let bytes = captured.unwrap_or_else(|| {
        receiver
            .recv()
            .unwrap_or_else(|_| Err(io::Error::other("missing observation output")))
    })?;
    let interrupted = INTERRUPTED.load(Ordering::SeqCst);
    if !watch {
        if let Some(code) = completed_reply(&bytes, json) {
            io::stdout().write_all(&bytes)?;
            io::stdout().flush()?;
            return Ok(code);
        }
        if interrupted {
            if json {
                println!(
                    "{{\"ok\":false,\"code\":\"cancelled\",\"source\":\"client\",\"error\":\"cancelled\"}}"
                );
            }
        } else {
            io::stdout().write_all(&bytes)?;
        }
    }
    if interrupted {
        eprintln!("observation cancelled");
        Ok(130)
    } else {
        Ok(status.code().unwrap_or(1))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn detection_only_wraps_observation() {
        for (args, expected) in [
            (vec!["session", "wait", "completed"], Some(false)),
            (vec!["EVENTS", "--WATCH"], Some(true)),
            (
                vec![
                    "--json",
                    "session",
                    "wait",
                    "completed",
                    "--wait-timeout",
                    "0",
                ],
                Some(false),
            ),
            (
                vec![
                    "--pipe", "events", "--json", "session", "--window", "W", "wait", "idle",
                ],
                Some(false),
            ),
            (
                vec!["--json", "--pipe", "session", "events", "--watch"],
                Some(true),
            ),
            (vec!["--WATCH", "--window", "W", "EVENTS"], Some(true)),
            (vec!["--pipe", "events", "ping"], None),
            (vec!["events"], None),
            (vec!["session", "type", "wait"], None),
        ] {
            assert_eq!(
                mode(&args.into_iter().map(String::from).collect::<Vec<_>>()),
                expected
            );
        }
    }
    #[test]
    fn complete_outcome_wins_late_interrupt_without_duplicate_envelope() {
        assert_eq!(
            completed_reply(br#"{"ok":true,"result":{"outcome":"matched"}}"#, true),
            Some(0)
        );
        assert_eq!(
            completed_reply(br#"{"ok":true,"result":{"outcome":"timeout"}}"#, true),
            Some(3)
        );
        assert_eq!(
            completed_reply(br#"{"ok":false,"error":"gone"}"#, true),
            Some(1)
        );
        assert_eq!(completed_reply(b"completed\r\n", false), Some(0));
        assert_eq!(completed_reply(b"", true), None);
        assert_eq!(completed_reply(b"{\"ok\":", true), None);
    }
}
