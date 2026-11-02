//! Shared ZMX wire/identity primitives. No creation, ownership ledger or terminate API.
#![cfg(unix)]
use serde_json::{Value, json};
use std::os::{
    fd::{FromRawFd, OwnedFd},
    unix::{
        fs::{FileTypeExt, MetadataExt},
        net::UnixStream,
    },
};
#[cfg(target_os = "macos")]
use std::os::fd::AsRawFd;
use std::{
    fs,
    io::{self, Read, Write},
    path::Path,
    time::Duration,
};
pub type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn process_identity_is_stable_and_rejects_invalid_ids() {
        let pid = std::process::id() as i32;
        assert_eq!(
            process_identity(pid).unwrap(),
            process_identity(pid).unwrap()
        );
        assert!(process_identity(0).is_err());
        assert!(process_identity(1).is_err());
    }
    #[test]
    fn wire_checks_tag_and_size_before_allocating() {
        for (tag, length) in [(25u8, 1u32), (24, 1048577), (24, 0)] {
            let (channel, mut peer) = UnixStream::pair().unwrap();
            let mut daemon = ExactDaemon {
                channel,
                identity: Value::Null,
                clients: 0,
            };
            let mut header = [0; 8];
            header[0] = tag;
            header[1..5].copy_from_slice(&length.to_le_bytes());
            peer.write_all(&header).unwrap();
            assert!(daemon.receive(24, 24, 1048576).is_err());
        }
    }
    #[test]
    fn missing_socket_cannot_create_a_workload() {
        let root = std::env::temp_dir().join(format!("aw-zmx-unit-{}", std::process::id()));
        assert!(!root.exists());
        assert!(ExactDaemon::open(&root, &std::env::current_exe().unwrap()).is_err());
        assert!(!root.exists());
    }
}
pub struct ExactDaemon {
    pub channel: UnixStream,
    pub identity: Value,
    pub clients: u64,
}
impl ExactDaemon {
    pub fn open(path: &Path, executable: &Path) -> Result<Self> {
        let before = fs::symlink_metadata(path)?;
        if !before.file_type().is_socket() || before.uid() != unsafe { libc::geteuid() } {
            return Err("socket owner mismatch".into());
        }
        let channel = connect(path)?;
        let mut daemon = Self {
            channel,
            identity: Value::Null,
            clients: 0,
        };
        let (workload, parent) = daemon.info()?;
        // Linux SO_PEERCRED identifies the listener's creator before ZMX forks, not
        // necessarily its serving daemon. Linux retains the stage-1 double process/
        // inode observation (and the hook additionally pins the daemon with pidfd).
        #[cfg(target_os = "macos")]
        if peer_pid(&daemon.channel)? != parent {
            return Err("socket peer is not the observed daemon".into());
        }
        let (process, _) = process_identity(parent)?;
        if fs::canonicalize(process_path(parent)?)? != fs::canonicalize(executable)? {
            return Err("daemon executable mismatch".into());
        }
        let (again, parent_again) = daemon.info()?;
        if workload != again || parent != parent_again || process_identity(parent)?.0 != process {
            return Err("daemon changed during observation".into());
        }
        let after = fs::symlink_metadata(path)?;
        if (before.dev(), before.ino(), before.uid()) != (after.dev(), after.ino(), after.uid()) {
            return Err("socket replaced during observation".into());
        }
        daemon.identity = json!({"daemon":process,"workload":workload,
            "socket":[before.dev(),before.ino()],"path":path});
        Ok(daemon)
    }
    pub fn send(&mut self, tag: u8, payload: &[u8]) -> Result<()> {
        let mut header = [0; 8];
        header[0] = tag;
        header[1..5].copy_from_slice(&u32::try_from(payload.len())?.to_le_bytes());
        self.channel.write_all(&header)?;
        self.channel.write_all(payload)?;
        Ok(())
    }
    pub fn receive(&mut self, expected: u8, min: usize, max: usize) -> Result<Vec<u8>> {
        let mut header = [0; 8];
        self.channel.read_exact(&mut header)?;
        let len = u32::from_le_bytes(header[1..5].try_into()?) as usize;
        if header[0] != expected || !(min..=max).contains(&len) {
            return Err("unsupported daemon ABI".into());
        }
        let mut bytes = vec![0; len];
        self.channel.read_exact(&mut bytes)?;
        Ok(bytes)
    }
    pub fn info(&mut self) -> Result<(Value, i32)> {
        self.send(6, &[])?;
        let bytes = self.receive(6, 552, 552)?;
        self.clients = u64::from_le_bytes(bytes[..8].try_into()?);
        process_identity(i32::from_le_bytes(bytes[8..12].try_into()?))
    }
}
#[cfg(target_os = "macos")]
fn peer_pid(channel: &UnixStream) -> Result<i32> {
    let mut pid = 0i32;
    let mut length = std::mem::size_of_val(&pid) as libc::socklen_t;
    if unsafe {
        libc::getsockopt(
            channel.as_raw_fd(),
            0,
            libc::LOCAL_PEERPID,
            (&mut pid as *mut i32).cast(),
            &mut length,
        )
    } != 0
    {
        return Err(io::Error::last_os_error().into());
    }
    Ok(pid)
}
fn connect(path: &Path) -> Result<UnixStream> {
    use std::os::unix::ffi::OsStrExt;
    let mut address: libc::sockaddr_un = unsafe { std::mem::zeroed() };
    let bytes = path.as_os_str().as_bytes();
    if bytes.len() >= address.sun_path.len() || bytes.contains(&0) {
        return Err("invalid socket path".into());
    }
    address.sun_family = libc::AF_UNIX as _;
    #[cfg(target_os = "macos")]
    {
        address.sun_len = std::mem::size_of_val(&address) as _;
    }
    for (dst, src) in address.sun_path.iter_mut().zip(bytes) {
        *dst = *src as _;
    }
    let raw = unsafe { libc::socket(libc::AF_UNIX, libc::SOCK_STREAM, 0) };
    if raw < 0 {
        return Err(io::Error::last_os_error().into());
    }
    let fd = unsafe { OwnedFd::from_raw_fd(raw) };
    if unsafe { libc::fcntl(raw, libc::F_SETFD, libc::FD_CLOEXEC) } < 0
        || unsafe { libc::fcntl(raw, libc::F_SETFL, libc::O_NONBLOCK) } < 0
    {
        return Err(io::Error::last_os_error().into());
    }
    if unsafe {
        libc::connect(
            raw,
            (&address as *const libc::sockaddr_un).cast(),
            std::mem::size_of_val(&address) as _,
        )
    } < 0
    {
        if io::Error::last_os_error().raw_os_error() != Some(libc::EINPROGRESS) {
            return Err(io::Error::last_os_error().into());
        }
        let mut p = libc::pollfd {
            fd: raw,
            events: libc::POLLOUT,
            revents: 0,
        };
        if unsafe { libc::poll(&mut p, 1, 3000) } <= 0 {
            return Err("socket connect timed out".into());
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
#[cfg(target_os = "macos")]
pub fn process_identity(pid: i32) -> Result<(Value, i32)> {
    if pid <= 1 {
        return Err("invalid process ID".into());
    }
    let mut info: libc::proc_bsdinfo = unsafe { std::mem::zeroed() };
    let size = std::mem::size_of_val(&info);
    if unsafe {
        libc::proc_pidinfo(
            pid,
            libc::PROC_PIDTBSDINFO,
            0,
            (&mut info as *mut libc::proc_bsdinfo).cast(),
            size as _,
        )
    } != size as i32
    {
        return Err(io::Error::last_os_error().into());
    }
    if info.pbi_uid != unsafe { libc::geteuid() } || info.pbi_status == 5 {
        return Err("process owner/state mismatch".into());
    }
    let mut boot = [0u8; 128];
    let mut len = boot.len();
    if unsafe {
        libc::sysctlbyname(
            c"kern.bootsessionuuid".as_ptr(),
            boot.as_mut_ptr().cast(),
            &mut len,
            std::ptr::null_mut(),
            0,
        )
    } != 0
    {
        return Err(io::Error::last_os_error().into());
    }
    Ok((
        json!({"pid":pid,"startSeconds":info.pbi_start_tvsec,"startMicros":info.pbi_start_tvusec,
        "boot":std::str::from_utf8(&boot[..len])?.trim_end_matches('\0'),"uid":info.pbi_uid}),
        info.pbi_ppid as i32,
    ))
}
#[cfg(target_os = "macos")]
fn process_path(pid: i32) -> Result<std::path::PathBuf> {
    let mut bytes = [0u8; 4096];
    let n = unsafe { libc::proc_pidpath(pid, bytes.as_mut_ptr().cast(), bytes.len() as _) };
    if n <= 0 {
        return Err(io::Error::last_os_error().into());
    }
    Ok(std::str::from_utf8(&bytes[..n as usize])?
        .trim_end_matches('\0')
        .into())
}
#[cfg(target_os = "linux")]
pub fn process_identity(pid: i32) -> Result<(Value, i32)> {
    if pid <= 1 {
        return Err("invalid process ID".into());
    }
    let root = format!("/proc/{pid}");
    if fs::metadata(&root)?.uid() != unsafe { libc::geteuid() } {
        return Err("process owner mismatch".into());
    }
    let stat = fs::read_to_string(format!("{root}/stat"))?;
    let fields: Vec<_> = stat[stat.rfind(')').ok_or("invalid proc stat")? + 1..]
        .split_whitespace()
        .collect();
    if fields.first() == Some(&"Z") {
        return Err("process exited".into());
    }
    Ok((
        json!({"pid":pid,"start":fields.get(19).ok_or("missing process start")?,
        "boot":fs::read_to_string("/proc/sys/kernel/random/boot_id")?.trim(),"namespace":fs::read_link(format!("{root}/ns/pid"))?}),
        fields.get(1).ok_or("missing parent")?.parse()?,
    ))
}
#[cfg(target_os = "linux")]
fn process_path(pid: i32) -> Result<std::path::PathBuf> {
    Ok(fs::read_link(format!("/proc/{pid}/exe"))?)
}
