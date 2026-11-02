use super::*;
use std::os::unix::{fs::FileTypeExt, net::UnixStream};

pub(super) fn process_identity(pid: i32) -> Result<(Value, i32)> {
    if pid <= 1 {
        return Err("invalid process ID".into());
    }
    let root = format!("/proc/{pid}");
    let stat = fs::read_to_string(format!("{root}/stat"))?;
    let fields: Vec<_> = stat[stat.rfind(')').ok_or("invalid proc stat")? + 1..]
        .split_whitespace()
        .collect();
    if fields.first() == Some(&"Z") {
        return Err(io::Error::from_raw_os_error(libc::ESRCH).into());
    }
    Ok((
        json!({"pid":pid,"start":fields.get(19).ok_or("missing start time")?,
        "boot":fs::read_to_string("/proc/sys/kernel/random/boot_id")?.trim(),
        "namespace":fs::read_link(format!("{root}/ns/pid"))?}),
        fields.get(1).ok_or("missing parent")?.parse()?,
    ))
}
pub(super) fn pid(identity: &Value) -> Result<i32> {
    let value = identity["pid"].as_i64().ok_or("invalid identity PID")?;
    if !(2..=i32::MAX as i64).contains(&value) {
        return Err("invalid identity PID".into());
    }
    Ok(value as i32)
}
pub(super) fn gone(expected: &Value) -> Result<bool> {
    match process_identity(pid(expected)?) {
        Ok((actual, _)) => Ok(&actual != expected),
        Err(error) if disappeared(error.as_ref()) => Ok(true),
        Err(error) => Err(error),
    }
}
pub(super) fn pin_workload(expected: &Value) -> Result<OwnedFd> {
    let pid = pid(expected)?;
    let fd = pin_pid(pid)?;
    if process_identity(pid)?.0 != *expected {
        return Err("stale workload".into());
    }
    Ok(fd)
}
fn connect(path: &Path) -> Result<UnixStream> {
    use std::os::unix::ffi::OsStrExt;
    let bytes = path.as_os_str().as_bytes();
    let mut address: libc::sockaddr_un = unsafe { std::mem::zeroed() };
    if bytes.len() >= address.sun_path.len() || bytes.contains(&0) {
        return Err("invalid socket path".into());
    }
    address.sun_family = libc::AF_UNIX as _;
    for (dst, src) in address.sun_path.iter_mut().zip(bytes) {
        *dst = *src as _;
    }
    let raw = unsafe {
        libc::socket(
            libc::AF_UNIX,
            libc::SOCK_STREAM | libc::SOCK_CLOEXEC | libc::SOCK_NONBLOCK,
            0,
        )
    };
    if raw < 0 {
        return Err(io::Error::last_os_error().into());
    }
    let fd = unsafe { OwnedFd::from_raw_fd(raw) };
    let code = unsafe {
        libc::connect(
            raw,
            (&address as *const libc::sockaddr_un).cast(),
            std::mem::size_of_val(&address) as _,
        )
    };
    if code < 0 {
        let error = io::Error::last_os_error();
        // An AF_UNIX EAGAIN means the backlog is full, not a pending connection.
        if error.raw_os_error() != Some(libc::EINPROGRESS) {
            return Err(error.into());
        }
        let mut poll = libc::pollfd {
            fd: raw,
            events: libc::POLLOUT,
            revents: 0,
        };
        if unsafe { libc::poll(&mut poll, 1, 3000) } <= 0 {
            return Err("socket connection timed out".into());
        }
    }
    let stream = UnixStream::from(fd);
    if let Some(error) = stream.take_error()? {
        return Err(error.into());
    }
    stream.set_nonblocking(false)?;
    stream.set_read_timeout(Some(Duration::from_secs(3)))?;
    stream.set_write_timeout(Some(Duration::from_secs(3)))?;
    Ok(stream)
}
pub(super) struct ExactDaemon {
    pub channel: UnixStream,
    pub pidfd: OwnedFd,
    pub identity: Value,
    pub clients: u64,
}
impl ExactDaemon {
    pub fn open(path: &Path, executable: &str) -> Result<Self> {
        let before = fs::symlink_metadata(path)?;
        if !before.file_type().is_socket() || before.uid() != unsafe { libc::getuid() } {
            return Err("not a socket owned by the selected Linux user".into());
        }
        let mut channel = connect(path)?;
        let (workload, daemon, clients) = Self::info_on(&mut channel)?;
        let pid = pid(&daemon)?;
        let pidfd = pin_pid(pid)?;
        if fs::canonicalize(format!("/proc/{pid}/exe"))? != fs::canonicalize(executable)? {
            return Err("unexpected daemon executable".into());
        }
        let (again_workload, again_daemon, _) = Self::info_on(&mut channel)?;
        if workload != again_workload || daemon != again_daemon {
            return Err("identity changed while pinning daemon".into());
        }
        let after = fs::symlink_metadata(path)?;
        if (before.dev(), before.ino()) != (after.dev(), after.ino()) {
            return Err("socket replaced during acquisition".into());
        }
        Ok(Self {
            channel,
            pidfd,
            identity: json!({"workload":workload,"daemon":daemon,
            "socket":[before.dev(),before.ino()],"path":path}),
            clients,
        })
    }
    fn send_on(channel: &mut UnixStream, tag: u8, payload: &[u8]) -> Result<()> {
        let mut header = [0u8; 8];
        header[0] = tag;
        header[1..5].copy_from_slice(&u32::try_from(payload.len())?.to_le_bytes());
        channel.write_all(&header)?;
        channel.write_all(payload)?;
        Ok(())
    }
    pub fn header_on(channel: &mut UnixStream) -> Result<(u8, usize)> {
        let mut header = [0u8; 8];
        channel.read_exact(&mut header)?;
        Ok((
            header[0],
            u32::from_le_bytes(header[1..5].try_into()?) as usize,
        ))
    }
    fn info_on(channel: &mut UnixStream) -> Result<(Value, Value, u64)> {
        Self::send_on(channel, 6, &[])?;
        if Self::header_on(channel)? != (6, 552) {
            return Err("unsupported Info ABI".into());
        }
        let mut data = [0u8; 552];
        channel.read_exact(&mut data)?;
        let clients = u64::from_le_bytes(data[..8].try_into()?);
        let pid = i32::from_le_bytes(data[8..12].try_into()?);
        let (workload, parent) = process_identity(pid)?;
        Ok((workload, process_identity(parent)?.0, clients))
    }
    pub fn send(&mut self, tag: u8, payload: &[u8]) -> Result<()> {
        Self::send_on(&mut self.channel, tag, payload)
    }
    pub fn receive(&mut self, expected_tag: u8, min: usize, max: usize) -> Result<Vec<u8>> {
        let (tag, length) = Self::header_on(&mut self.channel)?;
        if tag != expected_tag || !(min..=max).contains(&length) {
            return Err("unsupported or oversized daemon response".into());
        }
        let mut bytes = vec![0; length];
        self.channel.read_exact(&mut bytes)?;
        Ok(bytes)
    }
    pub fn terminate(&mut self, expected: &Value) -> Result<&'static str> {
        if &self.identity != expected {
            return Err("stale binding".into());
        }
        let (workload, daemon, _) = Self::info_on(&mut self.channel)?;
        if workload != expected["workload"] || daemon != expected["daemon"] {
            return Err("daemon changed before mutation".into());
        }
        self.send(5, &[])?;
        Ok(if wait_pid(&self.pidfd, 5000)? {
            "terminated"
        } else {
            "uncertain"
        })
    }
}
