#!/usr/bin/env python3
"""Agwinterm launch/reload adapter for pinned html-doc-overlay templates."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import time


def ctl(*args):
    command = [os.environ.get("AGWINTERMCTL", "agwintermctl"), *args, "--json"]
    if pipe := os.environ.get("AGWINTERM_PIPE"):
        command += ["--pipe", pipe]
    if window := os.environ.get("AGWINTERM_WINDOW_ID"):
        command += ["--window", window]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", check=False)
    reply = json.loads(result.stdout) if result.stdout else {}
    if result.returncode or not reply.get("ok"):
        raise RuntimeError(reply.get("error") or result.stderr or "control request failed")
    return reply["result"]


def owner(session):
    tree = ctl("tree")
    for workspace in tree["workspaces"]:
        for node in workspace["sessions"]:
            if node["id"] == session:
                return node
    raise RuntimeError("exact session ID not found")


def show(file, session, javascript=False):
    file = str(Path(file).resolve(strict=True))
    node = owner(session)
    pages = node.get("htmlOverlays") or []
    if node.get("overlay") and not pages:
        raise RuntimeError("program overlay occupies this session (including when hidden)")
    if pages and os.path.normcase(pages[0].get("file") or "") == os.path.normcase(file):
        page_id = pages[0]["id"]
        ctl("session", "overlay", "reload", "--target", session)
    else:
        if pages:
            ctl("session", "overlay", "close", "--target", session)
        options = ["--js"] if javascript else []
        page_id = ctl("session", "overlay", "open", "--html", file, "--cwd", str(Path(file).parent), "--navigation",
                      "--size-percent", "95", "--target", session, *options)["pageID"]
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        pages = owner(session).get("htmlOverlays") or []
        page = next((p for p in pages if p["id"] == page_id), None)
        if not page:
            raise RuntimeError("page was closed while loading")
        if page["state"] == "failed":
            raise RuntimeError(page.get("error") or "page load failed")
        if page["state"] == "loaded":
            return page_id
        time.sleep(0.1)
    raise RuntimeError("page load timed out; inspect tree before retrying")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path)
    parser.add_argument("--target", default=os.environ.get("AGWINTERM_SESSION_ID"))
    parser.add_argument("--js", action="store_true")
    args = parser.parse_args()
    try:
        if not args.target:
            raise RuntimeError("set AGWINTERM_SESSION_ID or pass --target SESSION_ID")
        print(show(args.file, args.target, args.js))
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
