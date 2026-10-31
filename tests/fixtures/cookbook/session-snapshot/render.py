#!/usr/bin/env python3
"""Render the pinned standalone viewer; launch optionally, never execute restore text."""
import argparse
import json
from pathlib import Path
import sys

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("snapshot", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--target")
args = parser.parse_args()
snapshot = json.loads(args.snapshot.read_text(encoding="utf-8-sig"))
template = Path(__file__).with_name("assets").joinpath("page.html").read_text(encoding="utf-8")
payload = json.dumps(snapshot, ensure_ascii=False).replace("<", "\\u003c")
page = template.replace("/*__SNAPSHOT__*/null", payload)
page = page.replace("Copy a snippet and paste it into a bash or zsh shell in agterm; a session that is still open is selected instead of duplicated.",
                    "Copy restore text for inspection. This viewer never executes it; verify commands before running them yourself.")
args.output.write_text(page, encoding="utf-8")
if args.target:
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    from show import show
    print(show(args.output, args.target, javascript=True))
