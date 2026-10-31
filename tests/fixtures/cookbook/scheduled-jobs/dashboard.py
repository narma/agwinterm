#!/usr/bin/env python3
"""Local, in-memory scheduled-jobs web fixture. It never schedules or executes commands."""
import argparse
import html
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

JOBS = {"report": {"name": "Report", "enabled": True, "runs": 0},
        "backup": {"name": "Backup", "enabled": True, "runs": 0}}
REFRESHES = 0
PAGE = '''<!doctype html><meta charset="utf-8"><title>Scheduled jobs fixture</title>
<script src="/htmx.min.js"></script><style>body{font:18px system-ui;padding:2rem}button,input{font:inherit;margin:.4rem}article{border-bottom:1px solid #aaa;padding:1rem}</style>
<h1>Scheduled jobs fixture</h1><p>Actions update sample counters only.</p>
<nav><a href="/next">Next page</a> <a href="/redirect">Cross-origin redirect</a> <a href="file:///C:/Windows/win.ini">Local file link</a></nav>
<a id="other-site">Other site</a><script>document.getElementById('other-site').href='http://localhost:'+location.port+'/';</script>
<input aria-label="Filter jobs" name="q" hx-get="/jobs" hx-trigger="input changed delay:150ms" hx-target="#jobs">
<section id="jobs" hx-get="/jobs" hx-trigger="load, every 5s" hx-include="[name=q]" hx-swap="innerHTML"></section>'''


def rows(query):
    global REFRESHES
    REFRESHES += 1
    result = [f'<p>Refresh {REFRESHES}</p>']
    for identity, job in JOBS.items():
        if query.casefold() not in job["name"].casefold():
            continue
        result.append(f'<article><h2>{html.escape(job["name"])}</h2>'
                      f'<p>{"Enabled" if job["enabled"] else "Disabled"}; runs {job["runs"]}</p>'
                      f'<button hx-post="/api/jobs/{identity}/run" hx-target="#jobs">Run {job["name"]}</button>'
                      f'<button hx-post="/api/jobs/{identity}/toggle" hx-target="#jobs">Toggle {job["name"]}</button></article>')
    return "".join(result).encode()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, data, content="text/html; charset=utf-8", code=200):
        self.send_response(code)
        self.send_header("Content-Type", content)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        uri = urlsplit(self.path)
        if uri.path == "/":
            self.reply(PAGE.encode())
        elif uri.path == "/next":
            self.reply(PAGE.replace("<h1>Scheduled jobs fixture</h1>", "<h1>Next dashboard page</h1>").encode())
        elif uri.path == "/redirect":
            self.send_response(302)
            self.send_header("Location", f"http://localhost:{self.server.server_port}/")
            self.send_header("Content-Length", "0")
            self.end_headers()
        elif uri.path == "/htmx.min.js":
            self.reply(Path(__file__).with_name("htmx.min.js").read_bytes(), "text/javascript")
        elif uri.path == "/jobs":
            self.reply(rows(parse_qs(uri.query).get("q", [""])[0]))
        elif uri.path == "/state":
            self.reply(json.dumps({"jobs": JOBS, "refreshes": REFRESHES}).encode(), "application/json")
        else:
            self.reply(b"Not found", code=404)

    def do_POST(self):
        parts = urlsplit(self.path).path.strip("/").split("/")
        if len(parts) != 4 or parts[:2] != ["api", "jobs"] or parts[2] not in JOBS or parts[3] not in ("run", "toggle"):
            self.reply(b"Not found", code=404)
            return
        job = JOBS[parts[2]]
        if parts[3] == "toggle":
            job["enabled"] = not job["enabled"]
        elif job["enabled"]:
            job["runs"] += 1
        else:
            self.reply(b"Disabled job", code=400)
            return
        self.reply(rows(""))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0)
    args = parser.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"http://127.0.0.1:{server.server_port}/", flush=True)
    server.serve_forever()
