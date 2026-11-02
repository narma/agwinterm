//! Explicitly provisioned, account-private inbound agterm adapter. No SSH administration.
use super::*;
use std::{
    collections::BTreeSet,
    process::{Command, Stdio},
    time::Instant,
};

const MAX: usize = 4 * 1024 * 1024;
const GUARD: &str = "printf '%s\\n' 'agterm: remote session is gone'; exit 1";

// Endpoint authority must not depend on which duplicate JSON field a parser keeps.
struct Unique(Value);
impl<'de> serde::Deserialize<'de> for Unique {
    fn deserialize<D: serde::Deserializer<'de>>(
        deserializer: D,
    ) -> std::result::Result<Self, D::Error> {
        struct Visitor;
        impl<'de> serde::de::Visitor<'de> for Visitor {
            type Value = Unique;
            fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
                f.write_str("JSON without duplicate keys")
            }
            fn visit_map<M: serde::de::MapAccess<'de>>(
                self,
                mut map: M,
            ) -> std::result::Result<Unique, M::Error> {
                let mut result = serde_json::Map::new();
                while let Some(key) = map.next_key::<String>()? {
                    if result.contains_key(&key) {
                        return Err(serde::de::Error::custom("duplicate export field"));
                    }
                    result.insert(key, map.next_value::<Unique>()?.0);
                }
                Ok(Unique(Value::Object(result)))
            }
            fn visit_seq<S: serde::de::SeqAccess<'de>>(
                self,
                mut seq: S,
            ) -> std::result::Result<Unique, S::Error> {
                let mut result = Vec::new();
                while let Some(value) = seq.next_element::<Unique>()? {
                    result.push(value.0);
                }
                Ok(Unique(Value::Array(result)))
            }
            fn visit_str<E: serde::de::Error>(self, v: &str) -> std::result::Result<Unique, E> {
                Ok(Unique(json!(v)))
            }
            fn visit_bool<E: serde::de::Error>(self, v: bool) -> std::result::Result<Unique, E> {
                Ok(Unique(json!(v)))
            }
            fn visit_i64<E: serde::de::Error>(self, v: i64) -> std::result::Result<Unique, E> {
                Ok(Unique(json!(v)))
            }
            fn visit_u64<E: serde::de::Error>(self, v: u64) -> std::result::Result<Unique, E> {
                Ok(Unique(json!(v)))
            }
            fn visit_f64<E: serde::de::Error>(self, _: f64) -> std::result::Result<Unique, E> {
                Err(serde::de::Error::custom("nonintegral export field"))
            }
            fn visit_unit<E: serde::de::Error>(self) -> std::result::Result<Unique, E> {
                Ok(Unique(Value::Null))
            }
        }
        deserializer.deserialize_any(Visitor)
    }
}
fn decode(raw: &[u8]) -> Result<Value> {
    Ok(serde_json::from_slice::<Unique>(raw)?.0)
}

fn secure_directory(path: &Path) -> Result<()> {
    private(path, false)?;
    if fs::canonicalize(path)? != path {
        return Err("export directory contains a symlink or noncanonical component".into());
    }
    // An untrusted writable ancestor could swap the directory after validation. Root-owned
    // sticky temporary roots are safe: another account cannot rename our private child.
    for parent in path.ancestors().skip(1) {
        let metadata = fs::symlink_metadata(parent)?;
        let uid = unsafe { libc::getuid() };
        let sticky_root = metadata.uid() == 0 && metadata.mode() & libc::S_ISVTX != 0;
        if !metadata.is_dir()
            || (metadata.uid() != uid && metadata.uid() != 0)
            || (metadata.mode() & 0o022 != 0 && !sticky_root)
        {
            return Err("export directory has an untrusted ancestor".into());
        }
    }
    Ok(())
}
fn secure_json(path: &Path) -> Result<Value> {
    secure_directory(path.parent().ok_or("missing private parent")?)?;
    let file = fs::OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
        .open(path)?;
    let meta = file.metadata()?;
    if !meta.is_file()
        || meta.uid() != unsafe { libc::getuid() }
        || meta.mode() & 0o077 != 0
        || meta.len() > MAX as u64
    {
        return Err("export file must be private, bounded and owned by this user".into());
    }
    let mut raw = Vec::new();
    file.take(MAX as u64 + 1).read_to_end(&mut raw)?;
    if raw.len() > MAX {
        return Err("export file too large".into());
    }
    decode(&raw)
}
fn text<'a>(value: &'a Value, key: &str) -> Result<&'a str> {
    let text = string(value, key)?;
    if text.is_empty() || text.len() > 4096 || text.chars().any(char::is_control) {
        return Err("invalid export field".into());
    }
    Ok(text)
}
// Revocation needs trusted endpoint authority, even when its runtime is broken.
fn config_state(root: &Path) -> Result<Value> {
    secure_directory(root)?;
    let value = secure_json(&root.join("config.json"))?;
    fields(
        &value,
        &[
            "version",
            "uid",
            "machineId",
            "distribution",
            "user",
            "origin",
            "executable",
            "socketDirectory",
            "cli",
            "pipe",
        ],
    )?;
    if value["version"] != 1
        || value["uid"].as_u64() != Some(unsafe { libc::getuid() } as u64)
        || value["user"] != user_home()?.0
        || value["machineId"] != fs::read_to_string("/etc/machine-id")?.trim()
        || env::var("WSL_DISTRO_NAME").is_ok_and(|d| value["distribution"] != d)
    {
        return Err("export endpoint belongs to another Linux user or distribution".into());
    }
    for key in ["distribution", "user", "origin", "pipe"] {
        text(&value, key)?;
    }
    if text(&value, "pipe")?.contains(['/', '\\']) {
        return Err("invalid application control pipe".into());
    }
    for key in ["executable", "socketDirectory", "cli"] {
        linux_path(text(&value, key)?)?;
    }
    Ok(value)
}
fn config(root: &Path) -> Result<Value> {
    let value = config_state(root)?;
    if root.join("disabled").try_exists()? {
        return Err("origin export is disabled".into());
    }
    secure_directory(Path::new(text(&value, "socketDirectory")?))?;
    readiness::require(text(&value, "executable")?)?;
    Ok(value)
}

fn provision(root: &Path, mut value: Value) -> Result<Value> {
    fields(
        &value,
        &[
            "distribution",
            "user",
            "origin",
            "executable",
            "socketDirectory",
            "cli",
            "pipe",
        ],
    )?;
    linux_path(root.to_str().ok_or("invalid export path")?)?;
    // Require an existing private parent; never overwrite an installed CLI or existing endpoint.
    secure_directory(root.parent().ok_or("missing export parent")?)?;
    value["version"] = json!(1);
    value["uid"] = json!(unsafe { libc::getuid() });
    value["machineId"] = json!(fs::read_to_string("/etc/machine-id")?.trim());
    use std::os::unix::fs::DirBuilderExt;
    fs::DirBuilder::new().mode(0o700).create(root)?;
    let result = (|| -> Result<Value> {
        atomic_write(
            &root.join("config.json"),
            &serde_json::to_vec(&value)?,
            false,
            0o600,
        )?;
        let checked = config(root)?;
        // Prove the selected application is reachable without focusing or changing any window.
        relay(&checked, &["ping".into()])?;
        private(&root.join("manifests"), true)?;
        private(&root.join("identities"), true)?;
        let bytes = read_bytes(&env::current_exe()?, 32 * 1024 * 1024)?;
        atomic_write(&root.join("agtermctl"), &bytes, false, 0o700)?;
        Ok(
            json!({"root":root,"cli":root.join("agtermctl"),"capabilities":["tree","existing-only-attach","managed-leadership","screen"],"presentation":false}),
        )
    })();
    if result.is_err() {
        // Only files this exclusive directory creation could have made; never recurse.
        for name in ["config.json", "agtermctl"] {
            let _ = fs::remove_file(root.join(name));
        }
        for name in ["manifests", "identities"] {
            let _ = fs::remove_dir(root.join(name));
        }
        let _ = fs::remove_dir(root);
    }
    result
}

fn relay(config: &Value, args: &[String]) -> Result<Value> {
    let mut child = Command::new(text(config, "cli")?)
        .args(args)
        .args(["--pipe", text(config, "pipe")?, "--json", "--timeout", "22"])
        .env_remove("AGWINTERM_SESSION_ID")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    let stdout = child.stdout.take().ok_or("missing relay stdout")?;
    let stderr = child.stderr.take().ok_or("missing relay stderr")?;
    let (send, receive) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let mut raw = Vec::new();
        let result = stdout
            .take(MAX as u64 + 1)
            .read_to_end(&mut raw)
            .map(|_| raw);
        let _ = send.send(result);
    });
    std::thread::spawn(move || {
        let _ = io::copy(&mut stderr.take(65537), &mut io::sink());
    });
    let deadline = Instant::now() + Duration::from_secs(24);
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err("origin control timed out".into());
        }
        std::thread::sleep(Duration::from_millis(10));
    };
    let raw = receive
        .recv_timeout(Duration::from_secs(1))
        .map_err(|_| "incomplete origin control response")??;
    if raw.len() > MAX || !status.success() {
        return Err("origin control unavailable or refused".into());
    }
    let value = decode(&raw)?;
    if value["ok"] != true {
        return Err("origin refused control".into());
    }
    Ok(value["result"].clone())
}

fn windows(config: &Value) -> Result<Vec<String>> {
    let result = relay(config, &["window".into(), "list".into()])?;
    let rows = result["windows"]
        .as_array()
        .ok_or("invalid origin windows")?;
    if rows.len() > 64 {
        return Err("too many origin windows".into());
    }
    let mut ids = BTreeSet::new();
    for row in rows.iter().filter(|r| r["open"] == true) {
        let id = text(row, "id")?;
        if !ids.insert(id.to_owned()) {
            return Err("duplicate origin window".into());
        }
    }
    Ok(ids.into_iter().collect())
}

fn publish(
    root: &Path,
    config: &Value,
    sessions: Vec<Value>,
    bindings: Vec<Value>,
) -> Result<Value> {
    secure_directory(&root.join("manifests"))?;
    secure_directory(&root.join("identities"))?;
    let manifest = json!({"version":1,"scope":config,"sessions":sessions,"bindings":bindings});
    let bytes = serde_json::to_vec(&manifest)?;
    if bytes.len() > MAX {
        return Err("export manifest too large".into());
    }
    let digest = format!("{:x}", Sha256::digest(&bytes));
    let directory = root.join("manifests").join(digest);
    private(&directory, true)?;
    secure_directory(&directory)?;
    // Each daemon name is permanently bound to one incarnation in this provisioned endpoint.
    // The agterm screen command only carries a name, so rediscovery cannot authorize name reuse.
    for binding in manifest["bindings"].as_array().ok_or("invalid bindings")? {
        let name = text(binding, "name")?;
        daemon_name(name)?;
        let identity = json!({"name":name,"identity":binding["identity"],"scope":config});
        atomic_write(
            &root.join("identities").join(name),
            &serde_json::to_vec(&identity)?,
            false,
            0o600,
        )?;
    }
    atomic_write(&directory.join("manifest.json"), &bytes, false, 0o600)?;
    let executable = directory.join("attach");
    let source = fs::symlink_metadata(root.join("agtermctl"))?;
    if !source.is_file() || source.uid() != unsafe { libc::getuid() } || source.mode() & 0o077 != 0
    {
        return Err("invalid private adapter executable".into());
    }
    if let Err(e) = fs::hard_link(root.join("agtermctl"), &executable) {
        if e.kind() != io::ErrorKind::AlreadyExists {
            return Err(e.into());
        }
        let destination = fs::symlink_metadata(&executable)?;
        if source.ino() != destination.ino() || source.dev() != destination.dev() {
            return Err("conflicting attach executable".into());
        }
    }
    sync_directory(&directory)?;
    Ok(
        json!({"remote":{"endpoint":{"executable":executable,"socketDirectory":config["socketDirectory"]},"sessions":manifest["sessions"]}}),
    )
}

fn tree(root: &Path, config: &Value) -> Result<Value> {
    let initial = windows(config)?;
    let mut sessions = Vec::new();
    let mut bindings = Vec::new();
    let mut ids = BTreeSet::new();
    let mut names = BTreeSet::new();
    for window in &initial {
        let mut args: Vec<String> = ["zmx", "export", "tree", "--window", window]
            .map(str::to_owned)
            .to_vec();
        for (flag, key) in [
            ("distribution", "distribution"),
            ("user", "user"),
            ("origin", "origin"),
            ("executable", "executable"),
            ("socket-directory", "socketDirectory"),
        ] {
            args.extend([format!("--{flag}"), text(config, key)?.to_owned()]);
        }
        args.extend([
            "--adapter".into(),
            root.join("agtermctl")
                .to_str()
                .ok_or("invalid adapter path")?
                .into(),
        ]);
        let result = relay(config, &args)?;
        for row in result["remote"]["sessions"]
            .as_array()
            .ok_or("invalid origin sessions")?
        {
            if row["windowID"] != *window || !ids.insert(text(row, "id")?.to_owned()) {
                return Err("origin topology changed".into());
            }
            sessions.push(row.clone());
        }
        for binding in result["bindings"]
            .as_array()
            .ok_or("missing exact origin identities")?
        {
            if binding["window"] != *window
                || !ids.contains(text(binding, "session")?)
                || !names.insert(text(binding, "name")?.to_owned())
            {
                return Err("ambiguous origin binding".into());
            }
            // Recheck Linux identity locally before making its immutable export authority public.
            let name = text(binding, "name")?;
            daemon_name(name)?;
            let daemon = exact::ExactDaemon::open(
                &Path::new(text(config, "socketDirectory")?).join(name),
                text(config, "executable")?,
            )?;
            if daemon.identity != binding["identity"] {
                return Err("origin daemon changed during publication".into());
            }
            bindings.push(binding.clone());
        }
        if sessions.len() > 4096 || bindings.len() > 8192 {
            return Err("origin tree exceeds limit".into());
        }
    }
    if windows(config)? != initial {
        return Err("origin windows changed during discovery".into());
    }
    publish(root, config, sessions, bindings)
}

fn attachment_request(config: &Value, manifest: &Value, args: &[String]) -> Result<Value> {
    if args.len() != 2 && args.len() != 5
        || args.first().map(String::as_str) != Some("attach")
        || args.len() == 5 && (args[2] != "/bin/sh" || args[3] != "-c" || args[4] != GUARD)
    {
        return Err(
            "only existing agterm attachment is supported; trailing commands are refused".into(),
        );
    }
    let name = &args[1];
    daemon_name(name)?;
    if manifest["version"] != 1 || manifest["scope"] != *config {
        return Err("stale export scope".into());
    }
    let matches: Vec<_> = manifest["bindings"]
        .as_array()
        .ok_or("invalid manifest")?
        .iter()
        .filter(|r| r["name"] == *name)
        .collect();
    if matches.len() != 1 {
        return Err("daemon is not in this immutable export".into());
    }
    if env::var("ZMX_DIR").is_ok_and(|d| config["socketDirectory"] != d) {
        return Err("wrong export socket namespace".into());
    }
    let nonce = env::var("ZMX_MANAGED").unwrap_or(random_uuid()?);
    let claim = match env::var("ZMX_MANAGED_CLAIM").as_deref() {
        Ok("1") => true,
        Ok("") | Err(_) => false,
        _ => return Err("invalid managed claim".into()),
    };
    Ok(
        json!({"verb":"attach","executable":config["executable"],"socketDirectory":config["socketDirectory"],"name":name,"identity":matches[0]["identity"],"nonce":nonce,"claim":claim}),
    )
}

fn dispatch() -> Result<Value> {
    let executable = env::current_exe()?;
    let args: Vec<_> = env::args().skip(1).collect();
    if args.first().map(String::as_str) == Some("--zmx-export-provision") && args.len() == 2 {
        use std::io::BufRead;
        let mut raw = Vec::new();
        io::stdin().lock().take(65537).read_until(b'\n', &mut raw)?;
        if raw.len() > 65536 || raw.last() != Some(&b'\n') {
            return Err("incomplete or oversized provisioning request".into());
        }
        return provision(linux_path(&args[1])?, decode(&raw)?);
    }
    if args.first().map(String::as_str) == Some("--zmx-export-disable") && args.len() == 2 {
        let root = linux_path(&args[1])?;
        config_state(root)?;
        atomic_write(&root.join("disabled"), b"disabled\n", false, 0o600)?;
        return Ok(json!({"disabled":true}));
    }
    let directory = executable.parent().ok_or("missing adapter directory")?;
    if executable.file_name().is_some_and(|n| n == "attach") {
        let root = directory
            .parent()
            .and_then(Path::parent)
            .ok_or("invalid manifest directory")?;
        let config = config(root)?;
        let manifest = secure_json(&directory.join("manifest.json"))?;
        return lifecycle::attach(&attachment_request(&config, &manifest, &args)?);
    }
    let config = config(directory)?;
    if args == ["zmx", "tree", "--json"] {
        return tree(directory, &config);
    }
    if args.len() >= 4 && args[0] == "zmx" && args[1] == "screen" {
        let name = &args[2];
        daemon_name(name)?;
        let mut all = false;
        let mut json = false;
        let mut lines = None;
        let mut i = 3;
        while i < args.len() {
            match args[i].as_str() {
                "--json" if !json => json = true,
                "--all" if !all => all = true,
                "--lines" if lines.is_none() && i + 1 < args.len() => {
                    i += 1;
                    let n: usize = args[i].parse()?;
                    if n == 0 || n > 100000 {
                        return Err("invalid screen line count".into());
                    }
                    lines = Some(n);
                }
                _ => return Err("unsupported screen option".into()),
            }
            i += 1;
        }
        if !json {
            return Err("screen requires --json".into());
        }
        relay(&config, &["ping".into()])?;
        let identity = secure_json(&directory.join("identities").join(name))?;
        if identity["scope"] != config {
            return Err("stale screen scope".into());
        }
        let result = lifecycle::terminal(
            &json!({"verb":"screen","executable":config["executable"],
            "socketDirectory":config["socketDirectory"],"name":name,"identity":identity["identity"],"all":all || lines.is_some()}),
        )?;
        let text = string(&result, "text")?;
        let text = if let Some(n) = lines {
            let rows: Vec<_> = text.trim_end_matches('\n').split('\n').collect();
            rows[rows.len().saturating_sub(n)..].join("\n")
        } else {
            text.to_owned()
        };
        return Ok(json!({"text":text}));
    }
    // All remaining operations are explicitly unsupported until their exact contract is handled.
    Err("unsupported origin control (supported: zmx tree/screen --json)".into())
}

pub(crate) fn run() -> i32 {
    // Attachment execs the ZMX client and must not have a lifetime timeout.
    if !env::current_exe()
        .ok()
        .is_some_and(|p| p.file_name().is_some_and(|n| n == "attach"))
    {
        std::thread::spawn(|| {
            std::thread::sleep(Duration::from_secs(55));
            eprintln!("origin export deadline exceeded");
            std::process::exit(1);
        });
    }
    match dispatch() {
        Ok(result) => {
            println!("{}", json!({"ok":true,"result":result}));
            0
        }
        Err(error) => {
            println!("{}", json!({"ok":false,"error":error.to_string()}));
            1
        }
    }
}

#[cfg(test)]
mod tests;
