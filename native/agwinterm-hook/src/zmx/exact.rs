use super::*;
pub(super) use agwinterm_zmx::process_identity;
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
pub(super) struct ExactDaemon {
    core: agwinterm_zmx::ExactDaemon,
    pub pidfd: OwnedFd,
}
impl std::ops::Deref for ExactDaemon {
    type Target = agwinterm_zmx::ExactDaemon;
    fn deref(&self) -> &Self::Target {
        &self.core
    }
}
impl std::ops::DerefMut for ExactDaemon {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.core
    }
}
impl ExactDaemon {
    pub fn open(path: &Path, executable: &str) -> Result<Self> {
        let mut core = agwinterm_zmx::ExactDaemon::open(path, Path::new(executable))?;
        let process = pid(&core.identity["daemon"])?;
        let pidfd = pin_pid(process)?;
        let (workload, parent) = core.info()?;
        if parent != process
            || workload != core.identity["workload"]
            || process_identity(process)?.0 != core.identity["daemon"]
        {
            return Err("daemon changed while acquiring pidfd".into());
        }
        Ok(Self { core, pidfd })
    }
    pub fn terminate(&mut self, expected: &Value) -> Result<&'static str> {
        if &self.core.identity != expected {
            return Err("stale binding".into());
        }
        let (workload, parent) = self.core.info()?;
        if workload != expected["workload"] || process_identity(parent)?.0 != expected["daemon"] {
            return Err("daemon changed before mutation".into());
        }
        self.core.send(5, &[])?;
        Ok(if wait_pid(&self.pidfd, 5000)? {
            "terminated"
        } else {
            "uncertain"
        })
    }
}
