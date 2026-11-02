"""Called only after the routed upstream runtime suite passes."""
import hashlib
import json
from pathlib import Path
import sys

root = Path(sys.argv[1])
executable = root / "zmx/zig-out/bin/zmx"
with executable.open("rb") as stream:
    digest = hashlib.file_digest(stream, "sha256").hexdigest()
record = {"version": 1, "sha256": digest,
          "capabilities": ["managed-leadership", "authoritative-screen", "acknowledged-input",
                           "atomic-publication", "snapshot-replay", "existing-fd-attach", "primary-history-replay"],
          "provenance": (root / "provenance.txt").read_text(),
          "agwintermPatchSha256": {name: hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                                  for name in ("patch-existing-fd.py", "patch-primary-replay.py")}}
destination = Path(str(executable) + ".agwinterm-capabilities.json")
temporary = destination.with_suffix(".tmp")
temporary.write_text(json.dumps(record))
temporary.replace(destination)
print("PASS wrote capability evidence for", digest)
