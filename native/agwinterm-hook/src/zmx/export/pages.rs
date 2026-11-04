//! Scoped, non-PTY page stream. Idle pages are healthy; only blocked writes time out.
use super::*;
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};

fn session_from_token(token: &str) -> Result<&str> {
    let (session, nonce) = token.split_once('.').ok_or("invalid page token")?;
    uuid(session)?;
    if nonce.len() != 32 || !nonce.bytes().all(|c| c.is_ascii_hexdigit()) {
        return Err("invalid page token".into());
    }
    Ok(session)
}

fn copy(
    input: impl Read,
    mut output: impl Write,
    writing: Arc<AtomicU64>,
    start: Instant,
) -> io::Result<()> {
    use std::io::BufRead;
    let mut reader = io::BufReader::new(input);
    loop {
        let mut line = Vec::new();
        reader.by_ref().take(262145).read_until(b'\n', &mut line)?;
        if line.is_empty() {
            return Ok(());
        }
        if line.len() > 262144 || line.last() != Some(&b'\n') || std::str::from_utf8(&line).is_err()
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid page frame",
            ));
        }
        // Zero denotes a read wait. A page channel has no heartbeat or idle deadline.
        writing.store(start.elapsed().as_millis() as u64 + 1, Ordering::Release);
        output.write_all(&line)?;
        output.flush()?;
        writing.store(0, Ordering::Release);
    }
}

pub(super) fn run(root: &Path, config: &Value, token: &str) -> Result<Value> {
    let session_id = session_from_token(token)?;
    if unsafe { libc::isatty(0) } == 1 {
        return Err("page channel requires non-PTY SSH".into());
    }
    let inventory = tree(root, config)?;
    let session = inventory["remote"]["sessions"]
        .as_array()
        .ok_or("missing sessions")?
        .iter()
        .find(|row| row["id"] == session_id)
        .ok_or("exact page session unavailable")?;
    let mut args = presentation_args(root, config, session)?;
    args[2] = "page".into();
    args.extend(["--token".into(), token.into()]);
    let mut child = Command::new(text(config, "cli")?)
        .args(args)
        .env_remove("AGWINTERM_SESSION_ID")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    PRESENTING.store(true, Ordering::Relaxed);
    let stdin = child.stdin.take().ok_or("missing page stdin")?;
    let stdout = child.stdout.take().ok_or("missing page stdout")?;
    let stderr = child.stderr.take().ok_or("missing page stderr")?;
    let start = Instant::now();
    let writes = [Arc::new(AtomicU64::new(0)), Arc::new(AtomicU64::new(0))];
    let (send, receive) = std::sync::mpsc::channel();
    let sent = send.clone();
    let writing = writes[0].clone();
    std::thread::spawn(move || {
        let _ = sent.send(copy(io::stdin().lock(), stdin, writing, start));
    });
    let sent = send.clone();
    let writing = writes[1].clone();
    std::thread::spawn(move || {
        let _ = sent.send(copy(stdout, io::stdout().lock(), writing, start));
    });
    std::thread::spawn(move || {
        if !matches!(io::copy(&mut stderr.take(65537), &mut io::sink()), Ok(n) if n <= 65536) {
            let _ = send.send(Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "page stderr limit",
            )));
        }
    });
    let result = (|| -> Result<Value> {
        loop {
            if let Ok(result) = receive.recv_timeout(Duration::from_millis(100)) {
                result?;
                return Ok(Value::Null);
            }
            if let Some(status) = child.try_wait()? {
                return if status.success() {
                    Ok(Value::Null)
                } else {
                    Err("origin page unavailable".into())
                };
            }
            if root.join("disabled").try_exists()? || config_state(root)? != *config {
                return Err("page endpoint revoked or replaced".into());
            }
            let now = start.elapsed().as_millis() as u64 + 1;
            if writes.iter().any(|w| {
                let since = w.load(Ordering::Acquire);
                since != 0 && now.saturating_sub(since) > 5000
            }) {
                return Err("page write stalled".into());
            }
        }
    })();
    let _ = child.kill();
    let _ = child.wait();
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn tokens_require_exact_session_and_nonce() {
        let session = "11111111-1111-4111-8111-111111111111";
        assert_eq!(
            session_from_token(&format!("{session}.{}", "a".repeat(32))).unwrap(),
            session
        );
        for token in [
            session.to_string(),
            format!("{session}.short"),
            format!("{session}.{}", "g".repeat(32)),
            format!("{session}.{}.extra", "a".repeat(32)),
            format!("active.{}", "a".repeat(32)),
        ] {
            assert!(session_from_token(&token).is_err());
        }
    }
    #[test]
    fn relay_bounds_lines_and_rejects_truncation() {
        let mut output = Vec::new();
        let writing = Arc::new(AtomicU64::new(0));
        copy(
            &b"{\"kind\":\"end\",\"id\":1}\n"[..],
            &mut output,
            writing.clone(),
            Instant::now(),
        )
        .unwrap();
        assert_eq!(writing.load(Ordering::Relaxed), 0);
        assert_eq!(output, b"{\"kind\":\"end\",\"id\":1}\n");
        assert!(copy(&b"{}"[..], io::sink(), writing.clone(), Instant::now()).is_err());
        assert!(
            copy(
                &vec![b'x'; 262145][..],
                io::sink(),
                writing.clone(),
                Instant::now()
            )
            .is_err()
        );
        assert!(copy(&b"\xff\n"[..], io::sink(), writing, Instant::now()).is_err());
    }
}
