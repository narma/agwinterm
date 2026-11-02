//! Local WSL session control. Never a PTY host or an implicit dependency installer.
use base64::Engine;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::os::{
    fd::{AsRawFd, FromRawFd, OwnedFd},
    unix::fs::{MetadataExt, OpenOptionsExt, PermissionsExt},
};
use std::{
    env, fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::Duration,
};

mod exact;
pub(crate) mod export;
mod ledger;
mod lifecycle;
mod readiness;
#[cfg(test)]
mod tests;

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
#[derive(Debug)]
struct RecoveryError {
    state: &'static str,
    message: String,
}
impl std::fmt::Display for RecoveryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.message.fmt(f)
    }
}
impl std::error::Error for RecoveryError {}
fn fail<T>(state: &'static str, message: impl Into<String>) -> Result<T> {
    Err(Box::new(RecoveryError {
        state,
        message: message.into(),
    }))
}
fn string<'a>(value: &'a Value, key: &str) -> Result<&'a str> {
    value
        .get(key)
        .and_then(Value::as_str)
        .ok_or_else(|| format!("missing or invalid {key}").into())
}
fn fields(value: &Value, expected: &[&str]) -> Result<()> {
    let object = value.as_object().ok_or("expected an object")?;
    if object.len() != expected.len() || expected.iter().any(|k| !object.contains_key(*k)) {
        return Err("unknown or missing fields".into());
    }
    Ok(())
}
fn uuid(value: &str) -> Result<()> {
    if value.len() != 36
        || !value.bytes().enumerate().all(|(i, c)| {
            if [8, 13, 18, 23].contains(&i) {
                c == b'-'
            } else {
                c.is_ascii_digit() || (b'a'..=b'f').contains(&c)
            }
        })
    {
        return Err("operation must be a canonical UUID".into());
    }
    Ok(())
}
fn random_uuid() -> Result<String> {
    let value = fs::read_to_string("/proc/sys/kernel/random/uuid")?
        .trim()
        .to_string();
    uuid(&value)?;
    Ok(value)
}
fn daemon_name(value: &str) -> Result<()> {
    if value.len() != 39
        || !value.starts_with("agterm-")
        || !value.as_bytes()[7..]
            .iter()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(c))
    {
        return Err("invalid daemon name".into());
    }
    Ok(())
}
fn linux_path(value: &str) -> Result<&Path> {
    let path = Path::new(value);
    if !path.is_absolute()
        || value.chars().any(|c| c < ' ')
        || path
            .components()
            .any(|c| c == std::path::Component::ParentDir)
    {
        return Err("expected absolute Linux path".into());
    }
    Ok(path)
}
fn private(path: &Path, create: bool) -> Result<()> {
    if path.starts_with("/mnt") {
        return Err("private state requires a Linux filesystem".into());
    }
    linux_path(path.to_str().ok_or("invalid path encoding")?)?;
    if create {
        use std::os::unix::fs::DirBuilderExt;
        fs::DirBuilder::new()
            .recursive(true)
            .mode(0o700)
            .create(path)?;
    }
    let info = fs::symlink_metadata(path)?;
    if !info.is_dir() || info.uid() != unsafe { libc::getuid() } || info.mode() & 0o077 != 0 {
        return Err("directory must be private and owned by this Linux user".into());
    }
    Ok(())
}
fn read_bytes(path: &Path, limit: usize) -> Result<Vec<u8>> {
    let file = fs::OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW)
        .open(path)?;
    let mut bytes = Vec::new();
    file.take(limit as u64 + 1).read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err("file exceeds supported size".into());
    }
    Ok(bytes)
}
fn read_json(path: &Path, limit: usize) -> Result<Value> {
    Ok(serde_json::from_slice(&read_bytes(path, limit)?)?)
}
fn sync_directory(path: &Path) -> Result<()> {
    fs::File::open(path)?.sync_all()?;
    Ok(())
}
fn atomic_write(path: &Path, bytes: &[u8], replace: bool, mode: u32) -> Result<()> {
    let parent = path.parent().ok_or("missing parent")?;
    let temporary = parent.join(format!(".pending-{}", random_uuid()?));
    let result = (|| -> Result<()> {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(mode)
            .open(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        #[cfg(test)]
        tests::publication_boundary(path, "before");
        if replace {
            fs::rename(&temporary, path)?;
        } else {
            match fs::hard_link(&temporary, path) {
                Ok(()) => (),
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {
                    if read_bytes(path, bytes.len())? != bytes {
                        return Err("conflicting immutable file".into());
                    }
                }
                Err(e) => return Err(e.into()),
            }
        }
        #[cfg(test)]
        tests::publication_boundary(path, "after");
        sync_directory(parent)?;
        Ok(())
    })();
    let _ = fs::remove_file(temporary);
    result
}
fn pin_pid(pid: i32) -> Result<OwnedFd> {
    if pid <= 1 {
        return Err("invalid PID".into());
    }
    let fd = unsafe { libc::syscall(libc::SYS_pidfd_open, pid, 0) } as i32;
    if fd < 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(unsafe { OwnedFd::from_raw_fd(fd) })
}
fn wait_pid(fd: &OwnedFd, timeout: i32) -> Result<bool> {
    let started = std::time::Instant::now();
    loop {
        let remaining = if timeout < 0 {
            -1
        } else {
            (timeout as u128).saturating_sub(started.elapsed().as_millis()) as i32
        };
        let mut poll = libc::pollfd {
            fd: fd.as_raw_fd(),
            events: libc::POLLIN,
            revents: 0,
        };
        let result = unsafe { libc::poll(&mut poll, 1, remaining) };
        if result >= 0 {
            return Ok(result > 0);
        }
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::Interrupted {
            return Err(error.into());
        }
    }
}
fn disappeared(error: &(dyn std::error::Error + 'static)) -> bool {
    error.downcast_ref::<io::Error>().is_some_and(|e| {
        matches!(
            e.kind(),
            io::ErrorKind::NotFound | io::ErrorKind::ConnectionRefused
        ) || e.raw_os_error() == Some(libc::ESRCH)
    })
}
fn user_home() -> Result<(String, PathBuf)> {
    let uid = unsafe { libc::getuid() };
    let mut entry: libc::passwd = unsafe { std::mem::zeroed() };
    let mut result = std::ptr::null_mut();
    let mut buffer = vec![0u8; 65536];
    let code = unsafe {
        libc::getpwuid_r(
            uid,
            &mut entry,
            buffer.as_mut_ptr().cast(),
            buffer.len(),
            &mut result,
        )
    };
    if code != 0 || result.is_null() {
        return Err("cannot resolve selected Linux user".into());
    }
    let name = unsafe { std::ffi::CStr::from_ptr(entry.pw_name) }
        .to_str()?
        .to_owned();
    let home = unsafe { std::ffi::CStr::from_ptr(entry.pw_dir) }
        .to_str()?
        .to_owned();
    Ok((name, PathBuf::from(home)))
}
fn install(request: &Value) -> Result<Value> {
    fields(request, &["endpoint"])?;
    let endpoint = string(request, "endpoint")?;
    if endpoint.len() != 24
        || !endpoint
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    {
        return Err("invalid endpoint".into());
    }
    let (user, home) = user_home()?;
    let bytes = read_bytes(&env::current_exe()?, 32 * 1024 * 1024)?;
    let digest = format!("{:x}", Sha256::digest(&bytes));
    let ledger = home.join(".local/state/agwinterm/zmx").join(endpoint);
    private(&ledger, true)?;
    let helpers = ledger.join("helpers").join(digest);
    private(&helpers, true)?;
    let helper = helpers.join("agwinterm-hook");
    atomic_write(&helper, &bytes, false, 0o700)?;
    let sockets = PathBuf::from(format!("/tmp/agw-{}-{endpoint}", unsafe { libc::getuid() }));
    private(&sockets, true)?;
    Ok(
        json!({"distribution":env::var("WSL_DISTRO_NAME")?, "user":user,
        "cwd":env::current_dir()?, "ledger":ledger, "helper":helper, "socketDirectory":sockets}),
    )
}
fn read_request(args: &[String]) -> Result<Value> {
    let mut raw = Vec::new();
    if args.len() == 2 && args[0] == "--request" && args[1].len() <= 87384 {
        raw = base64::engine::general_purpose::STANDARD.decode(&args[1])?;
        raw.push(b'\n');
    } else if args.is_empty() {
        use std::io::BufRead;
        io::stdin().lock().take(65537).read_until(b'\n', &mut raw)?;
    } else {
        return Err("invalid helper arguments".into());
    }
    if raw.len() > 65536 || raw.last() != Some(&b'\n') {
        return Err("request too large or incomplete".into());
    }
    Ok(serde_json::from_slice(&raw)?)
}
pub(super) fn run(operation: &str, args: Vec<String>) -> i32 {
    // Rust ignores SIGPIPE; a lost response must not kill an acquired lifetime lease.
    let result = (|| -> Result<()> {
        if operation == "--zmx-workload" {
            if args.len() != 2 {
                return Err("expected ledger and operation".into());
            }
            return lifecycle::workload(&args[0], &args[1]);
        }
        let request = read_request(&args)?;
        let (reply, lease) = if operation == "--zmx-install" {
            (install(&request)?, None)
        } else {
            lifecycle::dispatch(&request)?
        };
        let written = writeln!(io::stdout().lock(), "{reply}");
        if let Err(error) = written {
            if lease.is_none() || error.kind() != io::ErrorKind::BrokenPipe {
                return Err(error.into());
            }
        }
        if let Some(fd) = lease {
            wait_pid(&fd, -1)?;
        }
        Ok(())
    })();
    match result {
        Ok(()) => 0,
        Err(error) => {
            let state = error
                .downcast_ref::<RecoveryError>()
                .map(|e| e.state)
                .unwrap_or_else(|| {
                    if error
                        .downcast_ref::<io::Error>()
                        .is_some_and(|e| e.kind() == io::ErrorKind::PermissionDenied)
                    {
                        "unavailable"
                    } else {
                        "uncertain"
                    }
                });
            let _ = writeln!(
                io::stdout().lock(),
                "{}",
                json!({"ok":false,"state":state,"error":error.to_string()})
            );
            1
        }
    }
}
