"""Loopback-only web contract fixture. No external services or system mutations."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import sys


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.path.startswith('/redirect'):
            self.send_response(302)
            self.send_header('Location', f'http://localhost:{self.server.server_port}/state')
            self.end_headers()
            return
        body = '''<!doctype html><title>Navigation fixture</title>
<a href="/next">Next page</a><a href="/redirect">Clicked redirect</a>
<a href="about:blank">Blank page</a><button onclick="window.close()">Close page</button>
<button onclick="location.href='/state'">State page</button>
<input type=file aria-label="File input"><textarea aria-label="Paste target"></textarea>
<script>
if(location.pathname==='/state') (async()=>{
 const prior=localStorage.getItem('parity');localStorage.setItem('parity','saved');
 const cookie=document.cookie.includes('parity=saved');document.cookie='parity=saved;max-age=86400;path=/';
 const open=indexedDB.open('parity',1);open.onupgradeneeded=()=>open.result.createObjectStore('values');
 const db=await new Promise((ok,no)=>{open.onsuccess=()=>ok(open.result);open.onerror=()=>no(open.error)});
 const tx=db.transaction('values','readwrite'),store=tx.objectStore('values'),read=store.get('value');
 let previous;read.onsuccess=()=>{previous=read.result||null;store.put('saved','value')};
 await new Promise((ok,no)=>{tx.oncomplete=ok;tx.onerror=no});db.close();
 document.title=JSON.stringify({prior,cookie,indexed:previous,bridge:typeof window.agterm});
})();
</script>'''
        if self.path.startswith('/next'):
            body = body.replace('Navigation fixture', 'Next document')
        data = body.encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)


server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
with open(sys.argv[1], 'w', encoding='utf-8') as output:
    json.dump({'url': f'http://127.0.0.1:{server.server_port}/'}, output)
server.serve_forever()
