use super::*;
use std::time::Instant;

pub(super) struct Ledger {
    pub root: PathBuf,
    _lock: fs::File,
}
impl Ledger {
    pub fn open(directory: &str) -> Result<Self> {
        let root = linux_path(directory)?.to_path_buf();
        private(&root, true)?;
        let file = fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .mode(0o600)
            .custom_flags(libc::O_NOFOLLOW)
            .open(root.join("operations.lock"))?;
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if unsafe { libc::flock(file.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } == 0 {
                break;
            }
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::WouldBlock {
                return Err(error.into());
            }
            if Instant::now() >= deadline {
                return Err("endpoint operation already in flight".into());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        Ok(Self { root, _lock: file })
    }
    pub fn path(&self, operation: &str) -> Result<PathBuf> {
        uuid(operation)?;
        Ok(self.root.join(format!("{operation}.json")))
    }
    pub fn marker(&self, operation: &str) -> Result<PathBuf> {
        Ok(self.path(operation)?.with_extension("started"))
    }
    pub fn read(&self, operation: &str) -> Result<Option<Value>> {
        let record = match read_json(&self.path(operation)?, 65536) {
            Ok(record) => record,
            Err(error)
                if error
                    .downcast_ref::<io::Error>()
                    .is_some_and(|e| e.kind() == io::ErrorKind::NotFound) =>
            {
                return Ok(None);
            }
            Err(error) => return Err(error),
        };
        if record["version"].as_u64() != Some(1) || record["operation"] != operation {
            return Err("invalid operation record; manual reconciliation required".into());
        }
        Ok(Some(record))
    }
    pub fn write(&self, record: &Value) -> Result<()> {
        let path = self.path(string(record, "operation")?)?;
        let bytes = serde_json::to_vec(record)?;
        if bytes.len() > 65536 {
            return Err("oversized operation record".into());
        }
        atomic_write(&path, &bytes, true, 0o600)?;
        if record["name"].is_string() && !record["identity"].is_null() && record["external"] != true
        {
            self.index_owner(record)?;
        }
        Ok(())
    }
    pub fn index_owner(&self, record: &Value) -> Result<()> {
        let name = string(record, "name")?;
        daemon_name(name)?;
        let operation = string(record, "operation")?;
        uuid(operation)?;
        let directory = self.root.join("owners");
        private(&directory, true)?;
        atomic_write(&directory.join(name), operation.as_bytes(), false, 0o600)
    }
    pub fn owner_operation(&self, name: &str) -> Result<Option<String>> {
        daemon_name(name)?;
        match read_bytes(&self.root.join("owners").join(name), 36) {
            Ok(bytes) => {
                let operation = String::from_utf8(bytes)?;
                uuid(&operation)?;
                Ok(Some(operation))
            }
            Err(error)
                if error
                    .downcast_ref::<io::Error>()
                    .is_some_and(|e| e.kind() == io::ErrorKind::NotFound) =>
            {
                Ok(None)
            }
            Err(error) => Err(error),
        }
    }
    pub fn reserve(&self, operation: &str, specification: &Value) -> Result<(Value, bool)> {
        let fingerprint = format!(
            "{:x}",
            Sha256::digest(python_json(specification)?.as_bytes())
        );
        if let Some(record) = self.read(operation)? {
            if record["fingerprint"] != fingerprint {
                return Err("operation ID reused with different launch data".into());
            }
            return Ok((record, false));
        }
        let name = format!("agterm-{}", random_uuid()?.replace('-', ""));
        let record = json!({"version":1,"operation":operation,"fingerprint":fingerprint,"name":name,"state":"reserved","specification":specification});
        self.write(&record)?;
        Ok((record, true))
    }
}

// Match json.dumps(sort_keys=True), including ASCII escaping and separators,
// so v1 records created by the previous helper retain their idempotency key.
pub(super) fn python_json(value: &Value) -> Result<String> {
    Ok(match value {
        Value::Null => "null".into(),
        Value::Bool(b) => b.to_string(),
        Value::Number(n) => {
            if !n.is_i64() && !n.is_u64() {
                return Err("nonintegral launch data".into());
            }
            n.to_string()
        }
        Value::String(s) => {
            let mut result = String::from("\"");
            for c in s.chars() {
                match c {
                    '"' => result.push_str("\\\""),
                    '\\' => result.push_str("\\\\"),
                    '\u{8}' => result.push_str("\\b"),
                    '\u{c}' => result.push_str("\\f"),
                    '\n' => result.push_str("\\n"),
                    '\r' => result.push_str("\\r"),
                    '\t' => result.push_str("\\t"),
                    ' '..='~' => result.push(c),
                    _ => {
                        for code in c.encode_utf16(&mut [0; 2]) {
                            result.push_str(&format!("\\u{code:04x}"));
                        }
                    }
                }
            }
            result.push('"');
            result
        }
        Value::Array(values) => format!(
            "[{}]",
            values
                .iter()
                .map(python_json)
                .collect::<Result<Vec<_>>>()?
                .join(", ")
        ),
        Value::Object(values) => {
            let mut keys: Vec<_> = values.keys().collect();
            keys.sort();
            format!(
                "{{{}}}",
                keys.into_iter()
                    .map(|key| Ok(format!(
                        "{}: {}",
                        python_json(&json!(key))?,
                        python_json(&values[key])?
                    )))
                    .collect::<Result<Vec<_>>>()?
                    .join(", ")
            )
        }
    })
}
