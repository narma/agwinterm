"""Add a bounded running-daemon build contract; hashing its disk path is insufficient."""
import json
from pathlib import Path
import sys

root = Path(sys.argv[1])
marker = (Path(__file__).resolve().parents[3] / 'native/agwinterm-remote/zmx-runtime-build.txt').read_text().strip()
ipc = root/'src/ipc.zig'
source = ipc.read_text()
assert source.count('    Type = 25,') == 1 and 'AwBuild' not in source
ipc.write_text(source.replace('    Type = 25,', '    Type = 25,\n    AwBuild = 26,'))
loop = root/'src/loop.zig'
source = loop.read_text()
needle = '                        .Screen => try daemon.handleScreen(gpa, client, &term, msg.payload),'
assert source.count(needle) == 1
source = source.replace(needle, needle + '''
                        .AwBuild => {
                            try ipc.appendMessage(gpa, &client.write_buf, .AwBuild,
                                if (msg.payload.len == 0) ''' + json.dumps(marker) + ''' else "");
                            client.has_pending_output = true;
                        },''')
loop.write_text(source)
