use agwinterm_zmx::ExactDaemon;
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::os::{fd::AsRawFd, unix::process::CommandExt};
use std::{
    fs,
    io::{self, Read},
    path::Path,
    process::Command,
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;

#[derive(serde::Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct Request {
    verb: String,
    executable: String,
    socket_directory: String,
    name: String,
    identity: Option<Value>,
    client_sha256: Option<String>,
    nonce: Option<String>,
    claim: Option<bool>,
    all: Option<bool>,
    text: Option<String>,
}

pub fn main() {
    let result = (|| -> Result<Value> {
        let args: Vec<_> = std::env::args().skip(1).collect();
        let raw = if args.len() == 2 && args[0] == "--request" {
            if args[1].len() > 32000 {
                return Err("request too large".into());
            }
            STANDARD.decode(&args[1])?
        } else if args.is_empty() {
            let mut raw = Vec::new();
            io::stdin().take(65537).read_to_end(&mut raw)?;
            raw
        } else {
            return Err("invalid arguments".into());
        };
        if raw.len() > 65536 {
            return Err("request too large".into());
        }
        let request = serde_json::to_value(serde_json::from_slice::<Request>(&raw)?)?;
        dispatch(&request)
    })();
    match result {
        Ok(value) => println!("{}", json!({"ok":true,"result":value})),
        Err(error) => {
            let state = if error.to_string() == "origin ZMX build is not the verified client/daemon pair" {
                "unsupported"
            } else { "unavailable" };
            println!(
                "{}",
                json!({"ok":false,"state":state,"error":error.to_string()})
            );
            std::process::exit(1);
        }
    }
}
fn text<'a>(v: &'a Value, key: &str) -> Result<&'a str> {
    let s = v[key].as_str().ok_or("missing string field")?;
    if s.is_empty() || s.len() > 4096 || s.chars().any(char::is_control) {
        return Err("invalid string field".into());
    }
    Ok(s)
}
fn path<'a>(v: &'a Value, key: &str) -> Result<&'a Path> {
    let p = Path::new(text(v, key)?);
    if !p.is_absolute() {
        return Err("absolute origin path required".into());
    }
    Ok(p)
}
fn digest(path: &Path) -> Result<String> {
    let mut hash = Sha256::new();
    let mut file = fs::File::open(path)?;
    let mut buffer = [0; 65536];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
    }
    Ok(format!("{:x}", hash.finalize()))
}
fn client() -> Result<(std::path::PathBuf, String)> {
    let executable = std::env::current_exe()?
        .parent()
        .ok_or("missing bridge directory")?
        .join("zmx-existing-client");
    let hash = digest(&executable)?;
    // Developer provisioning publishes this manifest only after the inherited-FD seam tests pass.
    let expected = fs::read_to_string(executable.with_extension("sha256"))?;
    if expected.trim() != hash {
        return Err("origin client build manifest mismatch".into());
    }
    Ok((executable, hash))
}
fn dispatch(request: &Value) -> Result<Value> {
    let verb = text(request, "verb")?;
    if !matches!(verb, "probe" | "attach" | "screen" | "type") {
        return Err("unsupported operation".into());
    }
    let allowed = [
        "verb",
        "executable",
        "socketDirectory",
        "name",
        "identity",
        "clientSha256",
        "nonce",
        "claim",
        "all",
        "text",
    ];
    if request
        .as_object()
        .ok_or("request must be an object")?
        .keys()
        .any(|k| !allowed.contains(&k.as_str()))
    {
        return Err("unknown request field".into());
    }
    let executable = path(request, "executable")?;
    let directory = path(request, "socketDirectory")?;
    let name = text(request, "name")?;
    if name.len() != 39
        || !name.starts_with("agterm-")
        || !name[7..]
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return Err("invalid daemon name".into());
    }
    let (client, client_hash) = client()?;
    // Replay is produced by the daemon, so a patched viewer alone cannot make a stock
    // daemon compatible. Accept only the explicitly provisioned, verified paired build.
    if digest(executable)? != client_hash {
        return Err("origin ZMX build is not the verified client/daemon pair".into());
    }
    let mut daemon = ExactDaemon::open(&directory.join(name), executable)?;
    daemon.identity["platform"] = json!(std::env::consts::OS);
    daemon.identity["executable"] = json!(fs::canonicalize(executable)?);
    if verb == "probe" {
        return Ok(
            json!({"identity":daemon.identity,"protocol":1,"clientSha256":client_hash,
            "existingOnly":true,"screen":true,"acknowledgedInput":true,"presentation":false,"terminate":false}),
        );
    }
    if daemon.identity != request["identity"]
        || request["clientSha256"].as_str() != Some(&client_hash)
    {
        return Err("origin incarnation changed".into());
    }
    match verb {
        "attach" => {
            let nonce = text(request, "nonce")?;
            if nonce.len() > 64
                || !nonce
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b == b'-')
            {
                return Err("invalid nonce".into());
            }
            let claim = request["claim"].as_bool().ok_or("claim must be boolean")?;
            daemon.channel.set_read_timeout(None)?;
            daemon.channel.set_write_timeout(None)?;
            let fd = daemon.channel.as_raw_fd();
            if fd < 3 || unsafe { libc::fcntl(fd, libc::F_SETFD, 0) } < 0 {
                return Err("cannot inherit verified socket".into());
            }
            let mut command = Command::new(client);
            command
                .args(["attach", name])
                .env_clear()
                .envs(std::env::vars().filter(|(k, _)| !k.starts_with("ZMX_")))
                .env("ZMX_DIR", directory)
                .env("ZMX_ATTACH_FD", fd.to_string())
                .env("ZMX_MANAGED", nonce)
                .env("ZMX_NO_DETACH_KEY", "1");
            if claim {
                command.env("ZMX_MANAGED_CLAIM", "1");
            }
            Err(command.exec().into())
        }
        "type" => {
            let input = request["text"].as_str().ok_or("input must be text")?;
            if input.is_empty() || input.len() > 32768 {
                return Err("input exceeds limit".into());
            }
            // Once any bytes are sent, all failures are uncertain; never resend.
            if daemon.send(25, input.as_bytes()).is_err() {
                return Ok(json!({"accepted":false,"uncertain":true}));
            }
            match daemon.receive(25, 1, 1) {
                Ok(ack) if ack == [0] => Ok(json!({"accepted":true,"executionComplete":false})),
                Ok(_) => Err("input rejected".into()),
                Err(_) => Ok(json!({"accepted":false,"uncertain":true})),
            }
        }
        "screen" => {
            let all = request["all"].as_bool().ok_or("all must be boolean")?;
            daemon.send(24, &[u8::from(all)])?;
            let bytes = daemon.receive(24, 24, 1048576)?;
            let n = |offset| u16::from_le_bytes([bytes[offset], bytes[offset + 1]]);
            let (columns, rows, x, y, alternate) = (n(8), n(10), n(12), n(14), bytes[16]);
            if columns == 0 || rows == 0 || x >= columns || y >= rows || alternate > 1 {
                return Err("invalid geometry".into());
            }
            Ok(
                json!({"revision":u64::from_le_bytes(bytes[..8].try_into()?),"columns":columns,"rows":rows,
                "cursorColumn":x,"cursorRow":y,"alternate":alternate!=0,"text":std::str::from_utf8(&bytes[24..])?}),
            )
        }
        _ => unreachable!(),
    }
}
