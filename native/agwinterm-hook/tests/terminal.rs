#![cfg(target_os = "linux")]

use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Command, Stdio};

// Exercise the shipped wire format through a real controlling terminal. Keep
// stdin open until exit, just as the Windows session owner does.
#[test]
fn forwards_binary_input_resize_and_exit_status() {
    let script = r#"set -e
        test -t 0 && test -t 1 && test -t 2
        stty raw -echo
        stty size
        dd bs=5 count=1 iflag=fullblock status=none
        stty size
        exit 23"#;
    let mut process = Command::new("timeout")
        .args([
            "10",
            env!("CARGO_BIN_EXE_agwinterm-hook"),
            "--pty",
            "83",
            "27",
            "/bin/sh",
            "-c",
            script,
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = process.stdin.take().unwrap();
    let mut output = BufReader::new(process.stdout.take().unwrap());
    let mut ready = String::new();
    output.read_line(&mut ready).unwrap();
    assert_eq!(ready, "27 83\n");

    input.write_all(&[0, 0, 0, 0, 0, 98, 0, 31]).unwrap();
    let bytes = [0, 3, 0x1b, 0xff, 0x0a];
    input.write_all(&[0, 0, 0, 5]).unwrap();
    input.write_all(&bytes).unwrap();
    let mut received = Vec::new();
    output.read_to_end(&mut received).unwrap();
    assert_eq!(received, [&bytes[..], b"31 98\n"].concat());
    assert_eq!(process.wait().unwrap().code(), Some(23));
}
