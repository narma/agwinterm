import json
from pathlib import Path
import shlex
import sys

panes, denied = json.loads(sys.argv[1]), set(json.loads(sys.argv[2]))
denied.update(('sh', 'bash', 'zsh', 'fish', 'dash', 'nu', 'env', 'login', 'init', 'systemd'))
result = {}
for proc in Path('/proc').iterdir():
    if not proc.name.isdigit():
        continue
    try:
        stat = (proc / 'stat').read_text()
        fields = stat[stat.rfind(')') + 2:].split()
        # The foreground process group leader is the command, not its arbitrary tool children.
        if int(fields[4]) == 0 or int(fields[5]) != int(proc.name):
            continue
        environment = (proc / 'environ').read_bytes().split(b'\0')
        pane = next((p for p in panes if ('AGWINTERM_SESSION_ID=' + p).encode() in environment), None)
        if pane is None:
            continue
        argv = [s.decode('utf-8') for s in (proc / 'cmdline').read_bytes().split(b'\0') if s]
        if argv and Path(argv[0]).name not in denied:
            result[pane] = shlex.join(argv)
    except (FileNotFoundError, ProcessLookupError, PermissionError, UnicodeError):
        continue
print(json.dumps(result))
