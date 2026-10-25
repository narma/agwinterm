//! Obtain an exact ask ID before waiting so Linux SIGINT can cancel that identity.
use std::{
    io::{self, Read, Write},
    process::{Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};

pub(super) fn handles(args: &[String]) -> bool {
    if !args.first().is_some_and(|s| s.eq_ignore_ascii_case("ask"))
        || args
            .iter()
            .any(|s| matches!(s.as_str(), "--help" | "-h" | "--no-block"))
    {
        return false;
    }
    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "--window" | "--pipe" | "--socket" => i += 2,
            s if s.starts_with("--") => i += 1,
            s => return s == "open",
        }
    }
    true
}

fn transport(args: &[String]) -> Vec<String> {
    let mut result = Vec::new();
    let mut i = 1;
    while i < args.len() {
        if matches!(args[i].as_str(), "--pipe" | "--socket") && i + 1 < args.len() {
            result.extend_from_slice(&args[i..i + 2]);
            i += 2;
        } else if args[i] == "--window" {
            i += 2;
        } else {
            i += 1;
        }
    }
    result
}

fn call(
    cli: &str,
    args: &[String],
    input: bool,
    stop: Option<&AtomicBool>,
) -> io::Result<(i32, Vec<u8>)> {
    let mut child = Command::new(cli)
        .args(args)
        .stdin(if input {
            Stdio::inherit()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .spawn()?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| io::Error::other("missing ask stdout"))?;
    let (sender, receiver) = mpsc::sync_channel(1);
    let output = thread::spawn(move || {
        let mut bytes = Vec::new();
        let result = stdout
            .take(4 * 1024 * 1024 + 1)
            .read_to_end(&mut bytes)
            .and_then(|_| {
                if bytes.len() > 4 * 1024 * 1024 {
                    Err(io::Error::other("ask reply exceeds 4 MiB"))
                } else {
                    Ok(bytes)
                }
            });
        let _ = sender.send(result);
    });
    let start = Instant::now();
    let mut captured = None;
    let code = loop {
        if let Some(status) = child.try_wait()? {
            break status.code().unwrap_or(1);
        }
        if stop.is_some_and(|s| s.load(Ordering::SeqCst))
            || (!input && start.elapsed() > Duration::from_secs(20))
        {
            let _ = child.kill();
            let _ = child.wait();
            break 130;
        }
        if captured.is_none() {
            match receiver.try_recv() {
                Ok(result) => {
                    if result.is_err() {
                        let _ = child.kill();
                    }
                    captured = Some(result);
                }
                Err(mpsc::TryRecvError::Disconnected) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    break 1;
                }
                _ => {}
            }
        }
        thread::sleep(Duration::from_millis(10));
    };
    output
        .join()
        .map_err(|_| io::Error::other("ask output reader failed"))?;
    let bytes = captured.unwrap_or_else(|| {
        receiver
            .recv()
            .unwrap_or_else(|_| Err(io::Error::other("missing ask output")))
    })?;
    Ok((code, bytes))
}

pub(super) fn run(
    cli: &str,
    args: &[String],
    stop: &AtomicBool,
) -> Result<i32, Box<dyn std::error::Error>> {
    let mut open = args.to_vec();
    open.push("--no-block".into());
    let (code, bytes) = call(cli, &open, true, Some(stop))?;
    let id = serde_json::from_slice::<serde_json::Value>(&bytes)
        .ok()
        .and_then(|v| {
            v["id"]
                .as_str()
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
        });
    let Some(id) = id else {
        if stop.load(Ordering::SeqCst) {
            eprintln!("ask wait cancelled; open may have occurred without a known ID");
            return Ok(130);
        }
        io::stdout().write_all(&bytes)?;
        return Ok(if code == 0 { 1 } else { code });
    };
    let mut result_args = vec!["ask".into(), "result".into(), id.clone()];
    result_args.extend(transport(args));
    let result = (|| -> Result<i32, Box<dyn std::error::Error>> {
        let mut polls = 0;
        loop {
            if stop.load(Ordering::SeqCst) {
                return Ok(130);
            }
            let (code, bytes) = call(cli, &result_args, false, Some(stop))?;
            if stop.load(Ordering::SeqCst) && bytes.is_empty() {
                return Ok(130);
            }
            let outcome: serde_json::Value = serde_json::from_slice(&bytes).unwrap_or_default();
            match outcome["result"].as_str() {
                Some("answered") if code == 0 => {
                    io::stdout().write_all(&bytes)?;
                    return Ok(0);
                }
                Some("cancelled") if code == 2 => {
                    io::stdout().write_all(&bytes)?;
                    return Ok(2);
                }
                Some("pending") if code == 1 => {}
                _ => {
                    io::stdout().write_all(&bytes)?;
                    return Ok(1);
                }
            }
            polls += 1;
            for _ in 0..if polls <= 10 { 10 } else { 50 } {
                if stop.load(Ordering::SeqCst) {
                    return Ok(130);
                }
                thread::sleep(Duration::from_millis(10));
            }
        }
    })();
    if !matches!(&result, Ok(0 | 2)) {
        let mut cancel = vec!["ask".into(), "cancel".into(), id];
        cancel.extend(transport(args));
        let _ = call(cli, &cancel, false, None);
    }
    if matches!(&result, Ok(130)) {
        eprintln!("ask wait cancelled");
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    fn args(s: &[&str]) -> Vec<String> {
        s.iter().map(|s| s.to_string()).collect()
    }
    #[test]
    fn only_blocking_open_is_wrapped() {
        for a in [
            &["ask"][..],
            &["ask", "open"],
            &["ask", "--window", "result", "--follow"],
        ] {
            assert!(handles(&args(a)));
        }
        for a in [
            &["ask", "result", "id"][..],
            &["ask", "--no-block"],
            &["ask", "--help"],
            &["pick"],
        ] {
            assert!(!handles(&args(a)));
        }
    }
    #[test]
    fn polling_preserves_transport_but_not_moving_window() {
        assert_eq!(
            transport(&args(&[
                "ask", "--window", "active", "--pipe", "private", "--follow"
            ])),
            args(&["--pipe", "private"])
        );
    }
}
