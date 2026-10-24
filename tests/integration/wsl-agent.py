"""A disposable agent process for the real SessionStart/status hook round trip."""
import json
import os
from pathlib import Path
import subprocess

data = Path.home() / '.local/share/agwinterm'
hook = [str(data / 'agwinterm-hook')]
event = json.dumps({'session_id': 'wsl-test-conversation', 'cwd': os.getcwd()})
for state in ('bind', 'active', 'stop'):
    result = subprocess.run(hook + ['codex', state], input=event, text=True, check=True, capture_output=True)
    if state == 'bind':
        guidance = json.loads(result.stdout)['hookSpecificOutput']
        assert guidance['hookEventName'] == 'SessionStart'
        assert 'Do not call session rename' in guidance['additionalContext']
        assert 'session context' in guidance['additionalContext']
    else:
        assert not result.stdout, 'Status hooks must not inject repeated instructions'
print('WSL_HOOKS_FINISHED', flush=True)
