"""Apply the local existing-connection seam after the pinned agterm patches.

No wire ABI change: the helper authenticates Info on a socket and passes that
same connected descriptor through exec. This path must never ensureSession.
"""
from pathlib import Path
import sys

path = Path(sys.argv[1]) / "src/main.zig"
source = path.read_text()
old = '''    const sesh = socket.getSeshNameFromEnv();
    if (sesh.len > 0) {
        return switchSesh(gpa, io, daemon, sesh);
    }

    const is_daemon_proc = try daemon.ensureSession(io);
    if (is_daemon_proc) return;
'''
new = '''    // Agwinterm existing-only attachment: ownership/incarnation was checked
    // on this exact connected socket by the Linux helper before exec.
    const inherited_fd: ?i32 = if (lib_posix.getenv("ZMX_ATTACH_FD")) |value|
        try std.fmt.parseInt(i32, value, 10)
    else
        null;
    if (inherited_fd) |fd| {
        if (fd < 3 or labels != null) return error.InvalidInheritedAttachment;
    } else {
        const sesh = socket.getSeshNameFromEnv();
        if (sesh.len > 0) return switchSesh(gpa, io, daemon, sesh);
        const is_daemon_proc = try daemon.ensureSession(io);
        if (is_daemon_proc) return;
    }
'''
assert source.count(old) == 1, "unexpected attach implementation"
source = source.replace(old, new)
# Keep the upstream diagnostics unchanged; only replace the initializer.
needle = '    const client_sock = socket.sessionConnect(daemon.socket_path) catch |err| {'
start = source.index('fn attach(')
prefix, attachment = source[:start], source[start:]
assert attachment.count(needle) == 1
attachment = attachment.replace(needle, '    const client_sock = inherited_fd orelse socket.sessionConnect(daemon.socket_path) catch |err| {', 1)
needle = '        .switch_session => {\n'
assert needle in attachment
attachment = attachment.replace(needle, needle + '            if (inherited_fd != null) return error.ExistingAttachmentCannotSwitch;\n', 1)
path.write_text(prefix + attachment)
