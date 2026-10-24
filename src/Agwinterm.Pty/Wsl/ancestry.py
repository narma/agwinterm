import json
from pathlib import Path
import sys

pid, pane = int(sys.argv[1]), sys.argv[2]
root = Path('/proc') / str(pid)
if ('AGWINTERM_SESSION_ID=' + pane).encode() not in (root / 'environ').read_bytes().split(b'\0'):
    raise ValueError('Hook PID does not belong to the pane')
rows = []
for _ in range(64):
    try:
        root = Path('/proc') / str(pid)
        stat = (root / 'stat').read_text()
        ppid = int(stat[stat.rfind(')') + 2:].split()[1])
        argv = [s.decode('utf-8', errors='replace') for s in (root / 'cmdline').read_bytes().split(b'\0') if s]
        name = Path(argv[0]).name if argv else ''
        # ResumeFlags only retains conservative flag values; quote argv boundaries for its tokenizer.
        rows.append(dict(Pid=pid, ParentPid=ppid, Name=name, CommandLine=' '.join('"' + s.replace('"', '\\"') + '"' for s in argv)))
        if ppid <= 1 or ppid == pid:
            break
        pid = ppid
    except FileNotFoundError:
        break
print(json.dumps(rows))
