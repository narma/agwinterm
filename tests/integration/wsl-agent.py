"""A disposable agent process for the real SessionStart/status hook round trip."""
import json
import os
from pathlib import Path
import subprocess

hook = str(Path.home() / '.local/share/agwinterm/hook.py')
event = json.dumps({'session_id': 'wsl-test-conversation', 'cwd': os.getcwd()})
for state in ('bind', 'active', 'stop'):
    subprocess.run(['python3', hook, 'codex', state], input=event, text=True, check=True)
print('WSL_HOOKS_FINISHED', flush=True)
