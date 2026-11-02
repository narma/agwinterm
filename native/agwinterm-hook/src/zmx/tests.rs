use super::*;
#[test]
fn selected_commands_preserve_shell_options_and_replace_only_the_creation_command() {
    let cases: &[(&[&str], &[&str])] = &[
        (
            &[
                "/bin/bash",
                "--noprofile",
                "--norc",
                "-O",
                "extglob",
                "-c",
                "old",
                "arg0",
            ],
            &[
                "/bin/bash",
                "--noprofile",
                "--norc",
                "-O",
                "extglob",
                "-c",
                "selected",
            ],
        ),
        (&["zsh", "-lic", "old"], &["zsh", "-li", "-c", "selected"]),
        (
            &["bash", "--rcfile", "/tmp/rc file", "-o", "pipefail"],
            &[
                "bash",
                "--rcfile",
                "/tmp/rc file",
                "-o",
                "pipefail",
                "-c",
                "selected",
            ],
        ),
        (&["fish", "-l"], &["fish", "-l", "-c", "selected"]),
        (
            &["/bin/sh", "script", "old"],
            &["/bin/sh", "-c", "selected"],
        ),
        (
            &["/usr/bin/custom", "args"],
            &[
                "/bin/sh",
                "-c",
                "exec \"${SHELL:-/bin/sh}\" -lc \"$1\"",
                "agwinterm-restart",
                "selected",
            ],
        ),
    ];
    for (original, expected) in cases {
        let original: Vec<_> = original.iter().map(|s| s.to_string()).collect();
        assert_eq!(
            lifecycle::selected_command_argv(&original, "selected"),
            *expected
        );
    }
}
pub(super) fn publication_boundary(path: &Path, boundary: &str) {
    if path
        .parent()
        .and_then(Path::file_name)
        .is_some_and(|s| s == "owners")
        && env::var("AGW_TEST_OWNER_CRASH").is_ok_and(|s| s == boundary)
    {
        std::process::exit(73);
    }
}

#[test]
#[ignore = "subprocess crash boundary worker"]
fn owner_writer_child() {
    let root = env::var("AGW_TEST_OWNER_ROOT").unwrap();
    let record: Value = serde_json::from_str(&env::var("AGW_TEST_OWNER_RECORD").unwrap()).unwrap();
    ledger::Ledger::open(&root).unwrap().write(&record).unwrap();
    panic!("crash boundary was not reached");
}

#[test]
fn actual_process_exit_on_both_owner_publication_boundaries_recovers() {
    for boundary in ["before", "after"] {
        let root = Temporary::new();
        let operation = random_uuid().unwrap();
        let record = {
            let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
            let (mut record, _) = ledger.reserve(&operation, &json!({})).unwrap();
            record["identity"] = json!({"test":"committed"});
            record
        };
        let status = std::process::Command::new(env::current_exe().unwrap())
            .args(["--exact", "zmx::tests::owner_writer_child", "--ignored"])
            .env("AGW_TEST_OWNER_CRASH", boundary)
            .env("AGW_TEST_OWNER_ROOT", &root.0)
            .env("AGW_TEST_OWNER_RECORD", record.to_string())
            .status()
            .unwrap();
        assert_eq!(status.code(), Some(73));
        let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
        assert_eq!(ledger.read(&operation).unwrap(), Some(record.clone()));
        let name = string(&record, "name").unwrap();
        assert_eq!(
            ledger.owner_operation(name).unwrap(),
            if boundary == "after" {
                Some(operation.clone())
            } else {
                None
            }
        );
        ledger.write(&record).unwrap();
        assert_eq!(ledger.owner_operation(name).unwrap(), Some(operation));
    }
}
struct Temporary(PathBuf);
impl Temporary {
    fn new() -> Self {
        let path = PathBuf::from(format!("/tmp/agw-native-test-{}", random_uuid().unwrap()));
        private(&path, true).unwrap();
        Self(path)
    }
}
impl Drop for Temporary {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
#[test]
fn legacy_python_fingerprints_preserve_ascii_escaping_and_order() {
    let value = json!({"z":"😀\u{7f}","argv":["bash","\n\t\"\\"],"a":true});
    assert_eq!(
        ledger::python_json(&value).unwrap(),
        r#"{"a": true, "argv": ["bash", "\n\t\"\\"], "z": "\ud83d\ude00\u007f"}"#
    );
}
#[test]
fn ledger_retries_and_tombstones_never_create_again() {
    let root = Temporary::new();
    let operation = random_uuid().unwrap();
    let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
    let spec = json!({"argv":["sh"]});
    let (first, fresh) = ledger.reserve(&operation, &spec).unwrap();
    assert!(fresh);
    let (again, fresh) = ledger.reserve(&operation, &spec).unwrap();
    assert!(!fresh);
    assert_eq!(first, again);
    assert!(
        ledger
            .reserve(&operation, &json!({"argv":["other"]}))
            .is_err()
    );
    let cancelled = random_uuid().unwrap();
    ledger
        .write(&json!({"version":1,"operation":cancelled,"state":"cancelled"}))
        .unwrap();
    assert!(ledger.reserve(&cancelled, &spec).is_err());
}
#[test]
fn owner_index_is_complete_immutable_and_rebuildable() {
    let root = Temporary::new();
    let operation = random_uuid().unwrap();
    let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
    let (mut record, _) = ledger.reserve(&operation, &json!({})).unwrap();
    record["identity"] = json!({"pid":42});
    ledger.write(&record).unwrap();
    let name = string(&record, "name").unwrap();
    assert_eq!(
        ledger.owner_operation(name).unwrap(),
        Some(operation.clone())
    );
    // A crash before publication can leave a partial temporary file only.
    fs::write(root.0.join("owners/.pending-crashed"), b"partial").unwrap();
    fs::remove_file(root.0.join("owners").join(name)).unwrap();
    ledger.write(&record).unwrap();
    let mut conflicting = record.clone();
    conflicting["operation"] = json!(random_uuid().unwrap());
    assert!(ledger.index_owner(&conflicting).is_err());
    assert_eq!(ledger.owner_operation(name).unwrap(), Some(operation));
}
#[test]
fn partial_records_and_symlinks_fail_closed() {
    let root = Temporary::new();
    let operation = random_uuid().unwrap();
    let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
    let path = ledger.path(&operation).unwrap();
    fs::write(&path, b"{\"version\":").unwrap();
    assert!(ledger.read(&operation).is_err());
    fs::remove_file(&path).unwrap();
    let target = root.0.join("private");
    fs::write(&target, b"unchanged").unwrap();
    std::os::unix::fs::symlink(&target, &path).unwrap();
    assert!(ledger.read(&operation).is_err());
    assert_eq!(fs::read(target).unwrap(), b"unchanged");
}
#[test]
fn pidfd_identity_rejects_a_changed_incarnation() {
    let identity = exact::process_identity(std::process::id() as i32)
        .unwrap()
        .0;
    assert!(exact::pin_workload(&identity).is_ok());
    let mut stale = identity;
    stale["start"] = json!("0");
    assert!(exact::pin_workload(&stale).is_err());
    assert!(exact::gone(&stale).unwrap());
}
#[test]
fn unknown_fields_and_unsafe_identifiers_refuse() {
    assert!(
        lifecycle::dispatch(
            &json!({"verb":"reconcile","ledger":"/tmp","operation":"../x","unexpected":true})
        )
        .is_err()
    );
    assert!(uuid("../x").is_err());
    assert!(daemon_name("../x").is_err());
    assert!(linux_path("/tmp/../x").is_err());
}

#[test]
fn legacy_record_fingerprint_and_large_history_remain_usable() {
    let root = Temporary::new();
    let operation = random_uuid().unwrap();
    let ledger = ledger::Ledger::open(root.0.to_str().unwrap()).unwrap();
    // sha256(json.dumps({"argv": ["bash"]}, sort_keys=True)) from the old helper.
    let spec = json!({"argv":["bash"]});
    let python_bytes = b"{\"argv\": [\"bash\"]}";
    let record = json!({"version":1,"operation":operation,"name":format!("agterm-{}",random_uuid().unwrap().replace('-',"")),
        "fingerprint":format!("{:x}",Sha256::digest(python_bytes)),"state":"terminated","specification":spec});
    ledger.write(&record).unwrap();
    assert!(!ledger.reserve(&operation, &spec).unwrap().1);
    for _ in 0..1025 {
        let id = random_uuid().unwrap();
        ledger
            .write(&json!({"version":1,"operation":id,"state":"cancelled"}))
            .unwrap();
    }
    assert!(!ledger.reserve(&operation, &spec).unwrap().1);
}
