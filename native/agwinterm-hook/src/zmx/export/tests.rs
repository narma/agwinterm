use super::*;

#[test]
fn presentation_frames_are_bounded_and_never_complete_partial_lines() {
    let progress = std::sync::Arc::new(std::sync::atomic::AtomicU64::new(0));
    let mut output = Vec::new();
    let frame = b"{\"kind\":\"ping\",\"gen\":1,\"rev\":2}\n";
    relay_frames(&frame[..], &mut output, progress.clone(), Instant::now()).unwrap();
    assert_eq!(output, frame);
    for bad in [vec![b'x'; 262145], b"partial".to_vec(), vec![0xff, b'\n']] {
        let mut output = Vec::new();
        assert!(relay_frames(&bad[..], &mut output, progress.clone(), Instant::now()).is_err());
        assert!(output.is_empty());
    }
}

#[test]
fn presentation_relay_arguments_are_exact_and_do_not_forward_commands() {
    let config = json!({"distribution":"Ubuntu","user":"alice","origin":"origin",
        "executable":"/usr/bin/zmx","socketDirectory":"/tmp/zmx","pipe":"test"});
    let session = json!({"windowID":"11111111-1111-1111-1111-111111111111","id":"22222222-2222-2222-2222-222222222222"});
    let args = presentation_args(Path::new("/tmp/adapter"), &config, &session).unwrap();
    assert_eq!(&args[..3], &["zmx", "export", "present"]);
    assert!(args.windows(2).any(|p| p == ["--target", "22222222-2222-2222-2222-222222222222"]));
    assert!(args.windows(2).any(|p| p == ["--adapter", "/tmp/adapter/agtermctl"]));
    assert!(!args.contains(&"--json".into()));
    assert!(present(Path::new("/missing"), &config, "active").is_err());
}

struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        let root = env::temp_dir().join(format!("aw-export-unit-{}", random_uuid().unwrap()));
        private(&root, true).unwrap();
        Self(root)
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn manifest() -> (Value, Value) {
    let config = json!({"executable":"/missing-zmx","socketDirectory":"/tmp/missing-export"});
    let manifest = json!({"version":1,"scope":config,"bindings":[{"name":"agterm-11111111111111111111111111111111","identity":{"proof":"exact"}}]});
    (config, manifest)
}

#[test]
fn attachment_accepts_only_the_exact_upstream_guard_and_preserves_identity() {
    let (config, manifest) = manifest();
    let name = "agterm-11111111111111111111111111111111";
    for args in [
        vec!["attach", name],
        vec!["attach", name, "/bin/sh", "-c", GUARD],
    ] {
        let args: Vec<_> = args.into_iter().map(str::to_owned).collect();
        let request = attachment_request(&config, &manifest, &args).unwrap();
        assert_eq!(request["identity"], manifest["bindings"][0]["identity"]);
        assert_eq!(request["verb"], "attach");
        assert!(request.get("argv").is_none());
    }
    for args in [
        vec!["attach", name, "/bin/sh", "-c", "touch /tmp/forbidden"],
        vec!["kill", name],
        vec!["attach"],
        vec!["attach", "../other"],
        vec!["attach", "agterm-22222222222222222222222222222222"],
    ] {
        assert!(attachment_request(
            &config,
            &manifest,
            &args.into_iter().map(str::to_owned).collect::<Vec<_>>()
        )
        .is_err());
    }
}

#[test]
fn changed_scope_and_duplicate_daemon_refuse() {
    let (config, mut manifest) = manifest();
    let args = vec![
        "attach".into(),
        "agterm-11111111111111111111111111111111".into(),
    ];
    let mut other = config.clone();
    other["executable"] = json!("/other");
    assert!(attachment_request(&other, &manifest, &args).is_err());
    let row = manifest["bindings"][0].clone();
    manifest["bindings"].as_array_mut().unwrap().push(row);
    assert!(attachment_request(&config, &manifest, &args).is_err());
}

#[test]
fn private_files_refuse_symlinks_and_public_permissions() {
    use std::os::unix::fs::symlink;
    let fixture = Fixture::new();
    let file = fixture.0.join("private.json");
    atomic_write(&file, b"{}", false, 0o600).unwrap();
    assert_eq!(secure_json(&file).unwrap(), json!({}));
    symlink(&file, fixture.0.join("link.json")).unwrap();
    assert!(secure_json(&fixture.0.join("link.json")).is_err());
    fs::set_permissions(&file, fs::Permissions::from_mode(0o644)).unwrap();
    assert!(secure_json(&file).is_err());
    private(&fixture.0.join("real"), true).unwrap();
    symlink(fixture.0.join("real"), fixture.0.join("alias")).unwrap();
    assert!(secure_directory(&fixture.0.join("alias")).is_err());
    fs::set_permissions(fixture.0.join("real"), fs::Permissions::from_mode(0o777)).unwrap();
    private(&fixture.0.join("real/child"), true).unwrap();
    assert!(secure_directory(&fixture.0.join("real/child")).is_err());
}

#[test]
fn publication_is_immutable_and_name_reuse_cannot_replace_its_identity() {
    let fixture = Fixture::new();
    for name in ["manifests", "identities"] {
        private(&fixture.0.join(name), true).unwrap();
    }
    atomic_write(
        &fixture.0.join("agtermctl"),
        b"fake executable never run",
        false,
        0o700,
    )
    .unwrap();
    let (config, manifest) = manifest();
    let bindings = manifest["bindings"].as_array().unwrap().clone();
    let first = publish(&fixture.0, &config, vec![], bindings.clone()).unwrap();
    assert_eq!(
        publish(&fixture.0, &config, vec![], bindings.clone()).unwrap(),
        first
    );
    let mut replacement = bindings;
    replacement[0]["identity"] = json!({"proof":"replacement"});
    assert!(publish(&fixture.0, &config, vec![], replacement).is_err());
    let name = "agterm-11111111111111111111111111111111";
    assert_eq!(
        secure_json(&fixture.0.join("identities").join(name)).unwrap()["identity"],
        json!({"proof":"exact"})
    );
}

#[test]
fn missing_socket_never_runs_the_executable_or_trailing_command() {
    let fixture = Fixture::new();
    let executable = fixture.0.join("zmx");
    let sentinel = fixture.0.join("created");
    let bytes = format!("#!/bin/sh\ntouch '{}'\n", sentinel.display()).into_bytes();
    atomic_write(&executable, &bytes, false, 0o700).unwrap();
    atomic_write(&fixture.0.join("zmx.agwinterm-capabilities.json"), &serde_json::to_vec(&json!({
        "version":1,"sha256":format!("{:x}",Sha256::digest(&bytes)),
        "capabilities":["acknowledged-input","atomic-publication","authoritative-screen","existing-fd-attach","managed-leadership","primary-history-replay","snapshot-replay"]
    })).unwrap(), false, 0o600).unwrap();
    let config = json!({"executable":executable,"socketDirectory":fixture.0});
    let (_, mut manifest) = manifest();
    manifest["scope"] = config.clone();
    let request = attachment_request(
        &config,
        &manifest,
        &[
            "attach".into(),
            "agterm-11111111111111111111111111111111".into(),
        ],
    )
    .unwrap();
    assert!(lifecycle::attach(&request).is_err());
    assert!(!sentinel.exists());
}
