"""Local processes only; no packages, servers or sockets. Preserve old result files."""
import json
from pathlib import Path
import subprocess
import sys

here = Path(__file__).resolve().parent
output = here / "results" / (sys.argv[2] if len(sys.argv) > 2 else "cost_study.jsonl")
if output.exists():
    raise SystemExit(f"Refusing to overwrite {output}")
dotnet = sys.argv[1] if len(sys.argv) > 1 else "/usr/local/share/dotnet/dotnet"
dll = here / "bin/Release/net10.0/MemberProtocol.dll"
with output.open("x") as stream:
    for repeat in range(3):
        for count in (10000, 100000):
            for length in (16, 128):
                for mode, kinds in (("--memory", ("nodes", "dict", "owner-copy", "owner-share", "tree-baseline", "tree-owner")),
                                    ("--tree-cost", ("baseline", "owner", "scan"))):
                    for kind in kinds:
                        result = subprocess.run([dotnet, str(dll), mode, kind, str(count), str(length)],
                                                text=True, capture_output=True, check=True, timeout=60)
                        record = json.loads(result.stdout)
                        record.update(repeat=repeat + 1, mode=mode, variant=kind)
                        stream.write(json.dumps(record) + "\n")
                        stream.flush()
        print(f"Completed repeat {repeat + 1}", flush=True)
