"""Test-only capture against an explicitly isolated stage-4 peer and its bundled CLI."""
import hashlib
import base64
import fcntl
import json
import os
from pathlib import Path
import pty
import queue
import struct
import subprocess
import sys
import termios
import threading
import time

root = Path(sys.argv[1]).absolute()
if root.parent != Path('/tmp') or not root.name.startswith('aw-stage4-peer-'):
    raise SystemExit('isolated stage-4 peer required')
cli = root / 'agterm.app/Contents/MacOS/agtermctl'
sock = str(root / 'control.sock')
out = root / 'capture'
out.mkdir(mode=0o700, exist_ok=True)

def ctl(*args):
    result = subprocess.run([str(cli), *args, '--socket', sock, '--json'],
                            capture_output=True, text=True, timeout=15)
    if result.returncode: raise RuntimeError(result.stdout + result.stderr)
    return json.loads(result.stdout)

tree = ctl('zmx', 'tree')
session = tree['result']['remote']['sessions'][0]['id']
(out / 'tree.json').write_text(json.dumps(tree, indent=2))
records = []
children = []

class Channel:
    def __init__(self, args, name):
        self.name = name
        self.p = subprocess.Popen([str(cli), *args, '--socket', sock], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        children.append(self.p)
        self.q = queue.Queue()
        def read():
            for line in self.p.stdout:
                value = json.loads(line)
                records.append({'channel': name, 'direction': 'origin', 'frame': value})
                self.q.put(value)
        threading.Thread(target=read, daemon=True).start()
    def send(self, value):
        records.append({'channel': self.name, 'direction': 'viewer', 'frame': value})
        self.p.stdin.write(json.dumps(value) + '\n')
        self.p.stdin.flush()
    def until(self, kind):
        deadline = time.monotonic() + 15
        while True:
            value = self.q.get(timeout=max(.001, deadline - time.monotonic()))
            if value['kind'] == 'ping':
                self.send({'kind': 'ack', 'gen': value['gen'], 'rev': value['rev']})
            if value['kind'] == kind: return value

try:
    # Upstream delegates interactions only while the origin terminal is a follower.
    # Claim this isolated fixture's existing daemon explicitly, separately from presentation.
    remote = tree['result']['remote']
    base = dict(remote['endpoint'], name=remote['sessions'][0]['panes'][0]['daemon'])
    bridge = '/tmp/aw-stage2-build-20261102/bin/agwinterm-remote'
    probe = subprocess.run([bridge], input=json.dumps(dict(base, verb='probe')),
                           text=True, capture_output=True, timeout=15)
    result = json.loads(probe.stdout)
    assert probe.returncode == 0 and result['ok'], result
    attachment = dict(base, verb='attach', identity=result['result']['identity'],
                      clientSha256=result['result']['clientSha256'], nonce='stage4-capture', claim=True)
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    terminal = subprocess.Popen([bridge, '--request', base64.b64encode(json.dumps(attachment).encode()).decode()],
                                stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
    children.append(terminal)
    os.close(slave)
    def drain():
        try:
            while os.read(master, 65536): pass
        except OSError: pass
    threading.Thread(target=drain, daemon=True).start()
    deadline = time.monotonic() + 10
    while True:
        node = ctl('tree')['result']['tree']['workspaces'][0]['sessions'][0]
        if node['surfaces'][0].get('lead') == 'follower': break
        if time.monotonic() > deadline: raise RuntimeError('fixture did not become follower')
        time.sleep(.1)
    if node.get('ask'): ctl('ask', 'cancel', node['ask']['id'])
    ctl('session', 'context', '--clear', '--target', session)
    stream = Channel(['zmx', 'present', session], 'presentation')
    kinds = ['status', 'hud', 'notify', 'context', 'layout', 'ask.form', 'ask.other', 'overlay.points',
             'page.request', 'page.rejected', 'page.close', 'page.closed', 'page.command', 'page.state']
    stream.send({'kind': 'hello', 'gen': 0, 'rev': 0,
                 'hello': {'version': 1, 'kinds': kinds, 'mode': 'presenter'}})
    hello = stream.until('hello')
    stream.until('snapshot')
    gen = hello['gen']
    stream.send({'kind': 'presenter.acquire', 'gen': gen, 'rev': 0})
    stream.until('presenter.granted')
    ctl('session', 'status', 'active', '--target', session)
    stream.until('status')
    ctl('session', 'context', 'stage-4 protocol fixture', '--target', session)
    stream.until('context')
    ctl('ask', 'open', 'Stage-4 fixture', '--input', 'line', '--value', 'fixture', '--no-block', '--target', session)
    ask = stream.until('ask.request')['ask']
    stream.send({'kind': 'ask.resolve', 'gen': gen, 'rev': 0,
                 'ask': {'id': ask['id'], 'owner': ask['owner'], 'button': 'ok', 'text': 'fixture answer'}})
    stream.until('ask.dismiss')
    ctl('session', 'overlay', 'open', 'printf stage4-fixture', '--target', session)
    job = stream.until('overlay.request')['overlay']
    stream.send({'kind': 'overlay.rejected', 'gen': gen, 'rev': 0,
                 'overlay': {'job': job['job'], 'reason': 'fixture records request without execution'}})
    pagefile = root / 'fixture.html'
    pagefile.write_text('<!doctype html><title>Stage 4</title><p>fixture</p>')
    ctl('session', 'overlay', 'open', '--html', str(pagefile), '--cwd', str(root), '--target', session)
    page = stream.until('page.request')['page']
    pages = Channel(['session', 'overlay', 'page-serve', page['token']], 'page')
    pages.send({'kind': 'resource', 'id': 1, 'path': str(pagefile)})
    pages.until('end')
    stream.send({'kind': 'page.closed', 'gen': gen, 'rev': 0, 'page': {'page': page['page']}})
    deadline = time.monotonic() + 10
    while ctl('tree')['result']['tree']['workspaces'][0]['sessions'][0].get('overlay'):
        if time.monotonic() > deadline: raise RuntimeError('page slot did not close')
        time.sleep(.1)
    ctl('session', 'overlay', 'open', '--url', 'https://example.invalid/stage4', '--target', session)
    urlpage = stream.until('page.request')['page']
    stream.send({'kind': 'page.rejected', 'gen': gen, 'rev': 0,
                 'page': {'page': urlpage['page'], 'reason': 'fixture records URL without navigation'}})
finally:
    for child in children:
        child.terminate()
    for child in children:
        try: child.wait(timeout=5)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait(timeout=5)
    # Tokens are deliberately redacted even in this test output; original bytes are never published.
    for record in records:
        page = record['frame'].get('page')
        if isinstance(page, dict) and 'token' in page: page['token'] = 'REDACTED-SINGLE-USE-TOKEN'
    (out / 'frames.json').write_text(json.dumps(records, indent=2))
    manifest = {'revision': 'c63e5158e637662ded7ba3a12eac4881b24caef1',
                'binaries': {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                             for p in cli.parent.iterdir() if p.is_file()}}
    (out / 'provenance.json').write_text(json.dumps(manifest, indent=2))
print(json.dumps({'frames': len(records), 'output': str(out)}))
