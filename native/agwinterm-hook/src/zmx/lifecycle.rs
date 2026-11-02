use super::*;
use super::{
    exact::{ExactDaemon, gone, pin_workload, process_identity},
    ledger::Ledger,
};
use std::os::unix::process::CommandExt;
use std::{
    collections::{BTreeMap, BTreeSet},
    process::{Command, Stdio},
    time::Instant,
};

type Outcome = (Value, Option<OwnedFd>);
fn clean_environment(values: &Value) -> Result<BTreeMap<String, String>> {
    let values = values.as_object().ok_or("invalid environment")?;
    if values.len() > 256 {
        return Err("invalid environment".into());
    }
    let mut result: BTreeMap<_, _> = env::vars()
        .filter(|(k, _)| !k.starts_with("ZMX_") && k != "WSLENV")
        .collect();
    for (key, value) in values {
        let value = value.as_str().ok_or("invalid environment value")?;
        if key.is_empty() || key.contains(['=', '\0']) || value.contains('\0') {
            return Err("invalid environment entry".into());
        }
        if !key.starts_with("ZMX_") && key != "WSLENV" {
            result.insert(key.clone(), value.into());
        }
    }
    Ok(result)
}
fn argv(value: &Value) -> Result<Vec<String>> {
    let values = value.as_array().ok_or("invalid workload argv")?;
    if values.is_empty() || values.len() > 256 {
        return Err("invalid workload argv".into());
    }
    let values = values
        .iter()
        .map(|v| {
            v.as_str()
                .filter(|s| !s.contains('\0'))
                .map(str::to_owned)
                .ok_or_else(|| "invalid argument".into())
        })
        .collect::<Result<Vec<_>>>()?;
    if values[0].is_empty() {
        return Err("empty executable".into());
    }
    Ok(values)
}
fn validate_spec(spec: &Value) -> Result<()> {
    let mut keys = vec![
        "executable",
        "socketDirectory",
        "cwd",
        "argv",
        "environment",
    ];
    if spec.get("shellArgv").is_some() {
        keys.push("shellArgv");
    }
    fields(spec, &keys)?;
    for key in ["executable", "socketDirectory", "cwd"] {
        linux_path(string(spec, key)?)?;
    }
    argv(&spec["argv"])?;
    if let Some(shell) = spec.get("shellArgv") {
        argv(shell)?;
    }
    clean_environment(&spec["environment"])?;
    readiness::require(string(spec, "executable")?)?;
    let directory = string(spec, "socketDirectory")?;
    if directory.len() + 40 >= 108 {
        return Err("unsupported Linux socket directory".into());
    }
    private(Path::new(directory), true)
}
fn public(record: &Value) -> Value {
    let mut result = json!({});
    for key in [
        "operation",
        "name",
        "state",
        "identity",
        "guard",
        "error",
        "rollback",
    ] {
        if let Some(value) = record.get(key) {
            result[key] = value.clone();
        }
    }
    result
}
fn observe(record: &Value) -> Result<Value> {
    let spec = &record["specification"];
    let name = string(record, "name")?;
    daemon_name(name)?;
    let daemon = ExactDaemon::open(
        &Path::new(string(spec, "socketDirectory")?).join(name),
        string(spec, "executable")?,
    )?;
    if !record["identity"].is_null() && daemon.identity != record["identity"] {
        return Err("daemon incarnation replaced".into());
    }
    Ok(daemon.identity.clone())
}
pub(super) fn workload(directory: &str, operation: &str) -> Result<()> {
    let spec = {
        let ledger = Ledger::open(directory)?;
        let record = ledger
            .read(operation)?
            .ok_or("workload lacks reserved operation")?;
        if record["state"] != "launching" {
            return Err("workload lacks launch authorization".into());
        }
        // The marker is deliberately exclusive even if the process dies before exec.
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .custom_flags(libc::O_NOFOLLOW)
            .open(ledger.marker(operation)?)?;
        serde_json::to_writer(&mut file, &process_identity(std::process::id() as i32)?.0)?;
        file.sync_all()?;
        sync_directory(&ledger.root)?;
        record["specification"].clone()
    };
    let args = argv(&spec["argv"])?;
    Err(Command::new(&args[0])
        .args(&args[1..])
        .current_dir(string(&spec, "cwd")?)
        .env_clear()
        .envs(clean_environment(&spec["environment"])?)
        .exec()
        .into())
}
struct Attachment(std::process::Child);
impl Drop for Attachment {
    fn drop(&mut self) {
        if self.0.try_wait().ok().flatten().is_none() {
            // Only this temporary attach client, never the independently forked daemon.
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
}
fn create(request: &Value) -> Result<Outcome> {
    let spec = &request["specification"];
    validate_spec(spec)?;
    let operation = string(request, "operation")?;
    let directory = string(request, "ledger")?;
    let (record, fresh) = {
        let ledger = Ledger::open(directory)?;
        let (mut record, fresh) = ledger.reserve(operation, spec)?;
        if fresh {
            record["state"] = json!("launching");
            record["creator"] = process_identity(std::process::id() as i32)?.0;
            ledger.write(&record)?;
        }
        (record, fresh)
    };
    if !fresh {
        return reconcile(directory, operation);
    }
    let mut master = -1;
    let mut slave = -1;
    let size = libc::winsize {
        ws_row: 24,
        ws_col: 80,
        ws_xpixel: 0,
        ws_ypixel: 0,
    };
    if unsafe {
        libc::openpty(
            &mut master,
            &mut slave,
            std::ptr::null_mut(),
            std::ptr::null(),
            &size,
        )
    } != 0
    {
        return Err(io::Error::last_os_error().into());
    }
    let master = unsafe { OwnedFd::from_raw_fd(master) };
    let slave = unsafe { OwnedFd::from_raw_fd(slave) };
    for fd in [&master, &slave] {
        if unsafe { libc::fcntl(fd.as_raw_fd(), libc::F_SETFD, libc::FD_CLOEXEC) } < 0 {
            return Err(io::Error::last_os_error().into());
        }
    }
    let mut command = Command::new(string(spec, "executable")?);
    command
        .args(["attach", string(&record, "name")?])
        .arg(env::current_exe()?)
        .args(["--zmx-workload", directory, operation])
        .env_clear()
        .envs(clean_environment(&spec["environment"])?)
        .env("ZMX_DIR", string(spec, "socketDirectory")?)
        .env("ZMX_MANAGED", "creator")
        .env("ZMX_NO_DETACH_KEY", "1")
        .env("TERM", "xterm-256color")
        .stdin(Stdio::from(slave.try_clone()?))
        .stdout(Stdio::from(slave.try_clone()?))
        .stderr(Stdio::from(slave));
    unsafe {
        command.pre_exec(|| {
            if libc::setsid() < 0 {
                Err(io::Error::last_os_error())
            } else {
                Ok(())
            }
        });
    }
    let _child = Attachment(command.spawn()?);
    let deadline = Instant::now() + Duration::from_secs(8);
    while Instant::now() < deadline {
        let attempt = (|| -> Result<Outcome> {
            let ledger = Ledger::open(directory)?;
            let mut current = ledger.read(operation)?.ok_or("operation disappeared")?;
            let marker = read_json(&ledger.marker(operation)?, 65536)?;
            let identity = observe(&current)?;
            if marker != identity["workload"] {
                return Err("creation marker does not match workload".into());
            }
            let fd = pin_workload(&identity["workload"])?;
            current["state"] = json!("ready");
            current["identity"] = identity;
            current["guard"] = process_identity(std::process::id() as i32)?.0;
            ledger.write(&current)?;
            Ok((current, Some(fd)))
        })();
        match attempt {
            Ok(outcome) => return Ok(outcome),
            Err(error) if disappeared(error.as_ref()) => {
                std::thread::sleep(Duration::from_millis(50))
            }
            Err(error) => return Err(error),
        }
    }
    Err("creation is uncertain; reconcile the same operation".into())
}
fn reconcile(directory: &str, operation: &str) -> Result<Outcome> {
    let ledger = Ledger::open(directory)?;
    let mut record = ledger
        .read(operation)?
        .ok_or("unknown operation; reconcile never creates")?;
    if record["state"] == "terminated" {
        return Ok((record, None));
    }
    if !record["identity"].is_null() && gone(&record["identity"]["workload"])? {
        record["state"] = json!("missing");
        ledger.write(&record)?;
        return Ok((record, None));
    }
    readiness::require(string(&record["specification"], "executable")?)?;
    if record["state"] == "launching" && !record["creator"].is_null() && !gone(&record["creator"])?
    {
        return Ok((record, None));
    }
    let observation = (|| -> Result<Outcome> {
        let identity = observe(&record)?;
        if record["external"] != true
            && read_json(&ledger.marker(operation)?, 65536)? != identity["workload"]
        {
            return Err("ownership evidence unavailable".into());
        }
        let healthy = !record["guard"].is_null() && !gone(&record["guard"])?;
        let fd = if healthy {
            None
        } else {
            Some(pin_workload(&identity["workload"])?)
        };
        record["state"] = json!("ready");
        record["identity"] = identity;
        if !healthy {
            record["guard"] = process_identity(std::process::id() as i32)?.0;
        }
        ledger.write(&record)?;
        Ok((record.clone(), fd))
    })();
    match observation {
        Ok(outcome) => Ok(outcome),
        Err(error) if disappeared(error.as_ref()) => {
            record["state"] = json!(if !record["identity"].is_null()
                && gone(&record["identity"]["workload"])?
            {
                "missing"
            } else {
                "uncertain"
            });
            ledger.write(&record)?;
            Ok((record, None))
        }
        Err(error) => Err(error),
    }
}
fn restart(request: &Value) -> Result<Outcome> {
    let source = string(request, "source")?;
    if !["shell", "agent", "pin", "captured"].contains(&source) {
        return Err("unknown fresh-start source".into());
    }
    if source == "shell" {
        if !request["command"].is_null() {
            return Err("shell restart does not accept a saved command".into());
        }
    } else {
        let command = string(request, "command")?;
        if command.trim().is_empty() || command.contains('\0') || command.len() > 32768 {
            return Err("selected launch source is unavailable or invalid".into());
        }
    }
    let mut spec = {
        let ledger = Ledger::open(string(request, "ledger")?)?;
        let previous = owned_previous(&ledger, request)?;
        if !gone(&previous["identity"]["workload"])? {
            return Err("workload loss is not confirmed".into());
        }
        previous["specification"].clone()
    };
    let shell = spec.get("shellArgv").unwrap_or(&spec["argv"]).clone();
    spec["shellArgv"] = shell.clone();
    spec["argv"] = shell;
    if source != "shell" {
        spec["argv"] = json!(selected_command_argv(
            &argv(&spec["shellArgv"])?,
            string(request, "command")?
        ));
    }
    create(
        &json!({"ledger":request["ledger"],"operation":request["newOperation"],"specification":spec}),
    )
}
pub(super) fn selected_command_argv(original: &[String], command: &str) -> Vec<String> {
    let name = Path::new(&original[0]).file_name().and_then(|s| s.to_str());
    if !matches!(name, Some("bash" | "sh" | "dash" | "zsh" | "ksh" | "fish")) {
        return vec![
            "/bin/sh".into(),
            "-c".into(),
            "exec \"${SHELL:-/bin/sh}\" -lc \"$1\"".into(),
            "agwinterm-restart".into(),
            command.into(),
        ];
    }
    let mut result = vec![original[0].clone()];
    let mut value_pending = false;
    for arg in &original[1..] {
        if value_pending {
            result.push(arg.clone());
            value_pending = false;
            continue;
        }
        if arg == "-c" {
            break;
        }
        if arg.starts_with('-') && !arg.starts_with("--") && arg[1..].contains('c') {
            let remaining = arg.replace('c', "");
            if remaining != "-" {
                result.push(remaining);
            }
            break;
        }
        if !arg.starts_with('-') {
            break;
        }
        result.push(arg.clone());
        value_pending = matches!(arg.as_str(), "--rcfile" | "--init-file" | "-O" | "-o");
    }
    result.extend(["-c".into(), command.into()]);
    result
}
fn owned_previous(ledger: &Ledger, request: &Value) -> Result<Value> {
    let previous = ledger
        .read(string(request, "operation")?)?
        .ok_or("unknown origin operation")?;
    if previous["external"] == true
        || request["identity"].is_null()
        || previous["identity"] != request["identity"]
    {
        return Err("restart requires exact owned origin evidence".into());
    }
    if request["operation"] == request["newOperation"] {
        return Err("restart requires distinct operations".into());
    }
    uuid(string(request, "newOperation")?)?;
    Ok(previous)
}
fn reconcile_restart(request: &Value) -> Result<Outcome> {
    let directory = string(request, "ledger")?;
    let new = string(request, "newOperation")?;
    let rollback = {
        let ledger = Ledger::open(directory)?;
        owned_previous(&ledger, request)?;
        let pending = ledger.read(new)?;
        if pending.is_none() {
            ledger.write(&json!({"version":1,"operation":new,"state":"cancelled"}))?;
        }
        pending.is_none_or(|p| p["state"] == "cancelled")
    };
    let (mut record, fd) = reconcile(
        directory,
        if rollback {
            string(request, "operation")?
        } else {
            new
        },
    )?;
    record["rollback"] = json!(rollback);
    Ok((record, fd))
}
fn terminate(request: &Value) -> Result<Outcome> {
    let ledger = Ledger::open(string(request, "ledger")?)?;
    let mut record = ledger
        .read(string(request, "operation")?)?
        .ok_or("unknown operation")?;
    if record["external"] == true
        || request["identity"].is_null()
        || record["identity"] != request["identity"]
    {
        return Err("no exact owned operation".into());
    }
    if record["state"] == "terminated" {
        return Ok((record, None));
    }
    if gone(&record["identity"]["daemon"])?
        && (record["state"] == "terminating" || gone(&record["identity"]["workload"])?)
    {
        record["state"] = json!("terminated");
        ledger.write(&record)?;
        return Ok((record, None));
    }
    let spec = &record["specification"];
    let name = string(&record, "name")?;
    daemon_name(name)?;
    let mut daemon = ExactDaemon::open(
        &Path::new(string(spec, "socketDirectory")?).join(name),
        string(spec, "executable")?,
    )?;
    record["state"] = json!("terminating");
    ledger.write(&record)?;
    record["state"] = json!(daemon.terminate(&request["identity"])?);
    ledger.write(&record)?;
    Ok((record, None))
}
fn endpoint(request: &Value) -> Result<(&str, &Path)> {
    let executable = string(request, "executable")?;
    readiness::require(executable)?;
    let directory = linux_path(string(request, "socketDirectory")?)?;
    private(directory, false)?;
    Ok((executable, directory))
}
fn inventory(request: &Value) -> Result<Value> {
    let (executable, directory) = endpoint(request)?;
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut entries = Vec::new();
    for (index, entry) in fs::read_dir(directory)?.enumerate() {
        if index >= 1024 || Instant::now() >= deadline {
            return Err("inventory exceeds endpoint limit".into());
        }
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().to_string();
        if daemon_name(&name).is_ok() {
            entries.push((name, entry.path()));
        }
    }
    let mut owned = BTreeMap::new();
    if !entries.is_empty() && !request["ledger"].is_null() {
        let ledger = Ledger::open(string(request, "ledger")?)?;
        let mut unresolved = BTreeSet::new();
        for (name, _) in &entries {
            if let Some(operation) = ledger.owner_operation(name)? {
                let record = ledger.read(&operation)?.ok_or("missing indexed owner")?;
                if record["name"] != *name || record["external"] == true {
                    return Err("invalid daemon owner index".into());
                }
                owned.insert(name.clone(), record);
            } else {
                unresolved.insert(name.clone());
            }
        }
        if !unresolved.is_empty() {
            for entry in fs::read_dir(&ledger.root)? {
                if unresolved.is_empty() {
                    break;
                }
                if Instant::now() >= deadline {
                    return Err("legacy ownership lookup timed out".into());
                }
                let path = entry?.path();
                if path.extension().and_then(|s| s.to_str()) != Some("json") {
                    continue;
                }
                if let Some(record) = ledger.read(
                    path.file_stem()
                        .and_then(|s| s.to_str())
                        .ok_or("invalid record filename")?,
                )? {
                    if let Some(name) = record["name"].as_str() {
                        if record["external"] != true && unresolved.remove(name) {
                            ledger.index_owner(&record)?;
                            owned.insert(name.to_owned(), record);
                        }
                    }
                }
            }
        }
    }
    let mut sessions = Vec::new();
    for (name, path) in entries {
        if sessions.len() >= 64 || Instant::now() >= deadline {
            return Err("inventory exceeds endpoint limit".into());
        }
        let Ok(daemon) = ExactDaemon::open(&path, executable) else {
            continue;
        };
        let owner = owned
            .get(&name)
            .filter(|o| o["identity"] == daemon.identity);
        sessions.push(json!({"name":name,"identity":daemon.identity,"attachments":daemon.clients,
            "operation":owner.map(|o| &o["operation"]),"originPane":owner.map(|o| &o["specification"]["environment"]["AGWINTERM_SESSION_ID"])}));
    }
    Ok(json!({"state":"ready","sessions":sessions}))
}
fn borrow(request: &Value) -> Result<Outcome> {
    let (executable, directory) = endpoint(request)?;
    let name = string(request, "name")?;
    daemon_name(name)?;
    let daemon = ExactDaemon::open(&directory.join(name), executable)?;
    if daemon.identity != request["identity"] {
        return Err("external incarnation changed".into());
    }
    let operation = string(request, "operation")?;
    let root = string(request, "ledger")?;
    {
        let ledger = Ledger::open(root)?;
        let (mut record, fresh) = ledger.reserve(
            operation,
            &json!({"executable":executable,"socketDirectory":directory}),
        )?;
        if fresh {
            record["name"] = json!(name);
            record["external"] = json!(true);
            record["state"] = json!("ready");
            record["identity"] = daemon.identity.clone();
            ledger.write(&record)?;
        } else if record["external"] != true || record["identity"] != daemon.identity {
            return Err("external binding operation reused".into());
        }
    }
    reconcile(root, operation)
}
fn attach(request: &Value) -> Result<Value> {
    let (executable, directory) = endpoint(request)?;
    let name = string(request, "name")?;
    daemon_name(name)?;
    let nonce = string(request, "nonce")?;
    if nonce.is_empty()
        || nonce.len() > 64
        || !nonce
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'-')
    {
        return Err("invalid attachment nonce".into());
    }
    let claim = request
        .get("claim")
        .unwrap_or(&Value::Bool(false))
        .as_bool()
        .ok_or("claim must be boolean")?;
    let daemon = ExactDaemon::open(&directory.join(name), executable)?;
    if daemon.identity != request["identity"] {
        return Err("daemon incarnation replaced".into());
    }
    daemon.channel.set_read_timeout(None)?;
    daemon.channel.set_write_timeout(None)?;
    let fd = daemon.channel.as_raw_fd();
    if unsafe { libc::fcntl(fd, libc::F_SETFD, 0) } < 0 {
        return Err(io::Error::last_os_error().into());
    }
    let mut command = Command::new(executable);
    command
        .args(["attach", name])
        .env_clear()
        .envs(clean_environment(&json!({}))?)
        .env("ZMX_DIR", directory)
        .env("ZMX_ATTACH_FD", fd.to_string())
        .env("ZMX_MANAGED", nonce)
        .env("ZMX_NO_DETACH_KEY", "1");
    if claim {
        command.env("ZMX_MANAGED_CLAIM", "1");
    }
    Err(command.exec().into())
}
fn terminal(request: &Value) -> Result<Value> {
    let (executable, directory) = endpoint(request)?;
    let name = string(request, "name")?;
    daemon_name(name)?;
    let mut daemon = ExactDaemon::open(&directory.join(name), executable)?;
    if daemon.identity != request["identity"] {
        return Err("terminal incarnation changed".into());
    }
    if request["verb"] == "type" {
        let text = string(request, "text")?;
        if text.is_empty() || text.len() > 32768 {
            return Err("input must contain 1..32768 UTF-8 bytes".into());
        }
        daemon.send(25, text.as_bytes())?;
        let ack = match daemon.receive(25, 1, 1) {
            Ok(ack) => ack,
            Err(_) => {
                return fail(
                    "uncertain",
                    "input acknowledgement was not received; do not resend automatically",
                );
            }
        };
        if ack != [0] {
            return fail("unavailable", "daemon rejected input; nothing was queued");
        }
        return Ok(json!({"accepted":true,"executionComplete":false}));
    }
    let all = request["all"]
        .as_bool()
        .ok_or("screen mode must be boolean")?;
    daemon.send(24, &[u8::from(all)])?;
    let bytes = daemon.receive(24, 24, 1048576)?;
    let revision = u64::from_le_bytes(bytes[..8].try_into()?);
    let n = |offset| u16::from_le_bytes([bytes[offset], bytes[offset + 1]]);
    let (cols, rows, x, y, alternate) = (n(8), n(10), n(12), n(14), bytes[16]);
    if cols == 0 || rows == 0 || x >= cols || y >= rows || alternate > 1 {
        return Err("malformed daemon geometry".into());
    }
    Ok(
        json!({"revision":revision,"columns":cols,"rows":rows,"cursorColumn":x,"cursorRow":y,"alternate":alternate!=0,"text":std::str::from_utf8(&bytes[24..])?}),
    )
}
pub(super) fn dispatch(request: &Value) -> Result<Outcome> {
    let verb = string(request, "verb")?;
    let mut keys = match verb {
        "create" => vec!["verb", "ledger", "operation", "specification"],
        "reconcile" => vec!["verb", "ledger", "operation"],
        "terminate" => vec!["verb", "ledger", "operation", "identity"],
        "restart" => vec![
            "verb",
            "ledger",
            "operation",
            "newOperation",
            "identity",
            "source",
            "command",
        ],
        "reconcile-restart" => vec!["verb", "ledger", "operation", "newOperation", "identity"],
        "inventory" => vec!["verb", "executable", "socketDirectory"],
        "borrow" => vec![
            "verb",
            "executable",
            "socketDirectory",
            "name",
            "identity",
            "ledger",
            "operation",
        ],
        "screen" => vec![
            "verb",
            "executable",
            "socketDirectory",
            "name",
            "identity",
            "all",
        ],
        "type" => vec![
            "verb",
            "executable",
            "socketDirectory",
            "name",
            "identity",
            "text",
        ],
        "attach" => vec![
            "verb",
            "executable",
            "socketDirectory",
            "name",
            "identity",
            "nonce",
        ],
        "probe" => vec!["verb", "executable"],
        _ => return Err("unknown operation".into()),
    };
    if verb == "inventory" && request.get("ledger").is_some() {
        keys.push("ledger");
    }
    if verb == "attach" && request.get("claim").is_some() {
        keys.push("claim");
    }
    fields(request, &keys)?;
    let (result, fd) = match verb {
        "create" => create(request)?,
        "reconcile" => reconcile(string(request, "ledger")?, string(request, "operation")?)?,
        "restart" => restart(request)?,
        "reconcile-restart" => reconcile_restart(request)?,
        "terminate" => terminate(request)?,
        "borrow" => borrow(request)?,
        _ => {
            let value = match verb {
                "inventory" => inventory(request)?,
                "attach" => attach(request)?,
                "probe" => readiness::probe(string(request, "executable")?),
                _ => terminal(request)?,
            };
            return Ok((json!({"ok":true,"result":value}), None));
        }
    };
    Ok((json!({"ok":true,"result":public(&result)}), fd))
}
