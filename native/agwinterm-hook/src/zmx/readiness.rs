use super::*;
const REQUIRED: &[&str] = &[
    "acknowledged-input",
    "atomic-publication",
    "authoritative-screen",
    "existing-fd-attach",
    "managed-leadership",
    "primary-history-replay",
    "snapshot-replay",
];
pub(super) fn probe(executable: &str) -> Value {
    let mut result = json!({"version":1,"provider":"wsl-zmx","host":"server-rust",
        "distribution":env::var("WSL_DISTRO_NAME").ok(),"user":user_home().ok().map(|p| p.0),
        "uid":unsafe { libc::getuid() },"executable":executable,"remoteAttach":false,"presentation":false});
    let path = Path::new(executable);
    if !path.is_absolute()
        || !fs::metadata(path).is_ok_and(|m| m.is_file() && m.permissions().mode() & 0o111 != 0)
    {
        result["state"] = json!("missing-executable");
        result["reason"] = json!("Configured Linux executable is missing or not executable");
        return result;
    }
    let checked = (|| -> Result<()> {
        let _ = pin_pid(std::process::id() as i32)?;
        // Hash incrementally rather than retaining a potentially large binary.
        let file = fs::File::open(path)?;
        if file.metadata()?.len() > 268435456 {
            return Err("Executable exceeds supported size".into());
        }
        let mut input = file.take(268435457);
        let mut hash = Sha256::new();
        let mut bytes = [0u8; 65536];
        let mut total = 0usize;
        loop {
            let n = input.read(&mut bytes)?;
            if n == 0 {
                break;
            }
            total += n;
            hash.update(&bytes[..n]);
        }
        if total > 268435456 {
            return Err("Executable exceeds supported size".into());
        }
        let digest = format!("{:x}", hash.finalize());
        let attestation = read_json(
            Path::new(&format!("{executable}.agwinterm-capabilities.json")),
            16384,
        )?;
        if attestation["version"].as_u64() != Some(1) || attestation["sha256"] != digest {
            return Err("Executable has no matching runtime capability evidence".into());
        }
        let capabilities = attestation["capabilities"]
            .as_array()
            .ok_or("Malformed capability evidence")?;
        if capabilities.iter().any(|c| !c.is_string()) {
            return Err("Malformed capability evidence".into());
        }
        let missing: Vec<_> = REQUIRED
            .iter()
            .filter(|c| !capabilities.iter().any(|v| v.as_str() == Some(**c)))
            .collect();
        result["sha256"] = json!(digest);
        if !missing.is_empty() {
            result["state"] = json!("unsupported");
            result["missingCapabilities"] = json!(missing);
        } else {
            result["state"] = json!("ready");
            result["capabilities"] = json!(REQUIRED);
            result["provenance"] = attestation["provenance"].clone();
        }
        Ok(())
    })();
    if let Err(error) = checked {
        result["state"] = json!("unsupported");
        result["reason"] = json!(error.to_string());
    }
    result
}
pub(super) fn require(executable: &str) -> Result<()> {
    let result = probe(executable);
    if result["state"] == "ready" {
        return Ok(());
    }
    fail(
        if result["state"] == "unsupported" {
            "unsupported"
        } else {
            "unavailable"
        },
        result["reason"]
            .as_str()
            .map(str::to_owned)
            .unwrap_or_else(|| {
                format!(
                    "Missing ZMX capabilities: {}",
                    result["missingCapabilities"]
                )
            }),
    )
}
