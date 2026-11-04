//! Durable prompt checkpoints: kept by the PTY owner while its UI is detached.
use std::os::windows::ffi::OsStrExt;
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::atomic::{AtomicI64, Ordering},
};
use windows_sys::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
};

pub struct Completion {
    token: String,
    path: PathBuf,
    observed: AtomicI64,
}
impl Completion {
    pub fn from_env(env: &HashMap<String, String>) -> Option<Self> {
        let token = env.get("AGWINTERM_PROMPT_TOKEN")?;
        let path = env.get("AGWINTERM_COMPLETION_FILE")?;
        if token.len() != 32 || !token.bytes().all(|b| b.is_ascii_hexdigit()) || path.is_empty() {
            return None;
        }
        Some(Self {
            token: token.clone(),
            path: path.into(),
            observed: AtomicI64::new(0),
        })
    }
    pub fn record(&self, token: &str, sequence: i64) {
        if token != self.token || sequence <= 0 {
            return;
        }
        let previous = self.observed.fetch_max(sequence, Ordering::SeqCst);
        if sequence <= previous {
            return;
        }
        let write = || -> std::io::Result<()> {
            if let Some(parent) = self.path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            let temporary = self.path.with_extension("tmp");
            std::fs::write(&temporary, sequence.to_string())?;
            let wide = |p: &std::path::Path| {
                p.as_os_str()
                    .encode_wide()
                    .chain(Some(0))
                    .collect::<Vec<_>>()
            };
            if unsafe {
                MoveFileExW(
                    wide(&temporary).as_ptr(),
                    wide(&self.path).as_ptr(),
                    MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
                )
            } == 0
            {
                return Err(std::io::Error::last_os_error());
            }
            Ok(())
        };
        if let Err(error) = write() {
            eprintln!("Shell completion checkpoint: {error}");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn records_only_authenticated_increasing_prompts() {
        let token = "0123456789abcdef0123456789abcdef";
        let path =
            std::env::temp_dir().join(format!("agwinterm-completion-{}", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let checkpoint = Completion::from_env(&HashMap::from([
            ("AGWINTERM_PROMPT_TOKEN".into(), token.into()),
            (
                "AGWINTERM_COMPLETION_FILE".into(),
                path.to_string_lossy().into_owned(),
            ),
        ]))
        .unwrap();
        checkpoint.record("wrong", 999);
        assert!(!path.exists());
        checkpoint.record(token, 1);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "1");
        checkpoint.record(token, 3);
        checkpoint.record(token, 2);
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "3");
        std::fs::remove_file(path).unwrap();
    }
}
