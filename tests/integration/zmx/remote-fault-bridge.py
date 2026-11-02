#!/usr/bin/env python3
"""Test-only bridge fault injector. Installed only in an explicit isolated fixture directory."""
import base64
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent
if not str(root).startswith('/private/tmp/aw-stage2-fault-') and not str(root).startswith('/tmp/aw-stage2-fault-'):
    raise SystemExit('isolated fault fixture directory required')
config = json.loads((root/'config.json').read_text())
raw = base64.b64decode(sys.argv[2]) if len(sys.argv) == 3 and sys.argv[1] == '--request' else sys.stdin.buffer.read(65537)
request = json.loads(raw)
with (root/'requests.jsonl').open('a') as log:
    log.write(json.dumps({'verb': request['verb'], 'name': request['name']})+'\n')
if request['verb'] == 'attach' and request['name'] == config.get('refuseAttach'):
    raise SystemExit(1)
if request['verb'] == 'attach':
    import os
    os.execv(config['bridge'], [config['bridge'], '--request', base64.b64encode(raw).decode()])
result = subprocess.run([config['bridge']], input=raw, capture_output=True, timeout=15)
if request['verb'] == 'type' and config.get('dropInputReply'):
    # The real bridge has already delivered and acknowledged exactly one send.
    (root/'delivered.json').write_bytes(result.stdout)
    raise SystemExit(1)
sys.stdout.buffer.write(result.stdout)
raise SystemExit(result.returncode)
