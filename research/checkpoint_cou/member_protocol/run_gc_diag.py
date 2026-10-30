"""GC / slow-operation diagnosis and single-scan interference. Local processes only. Preserves old result files.

Conditions (each sets every relevant variable explicitly; inherited DOTNET_/COMPlus_ settings are removed):
  settle           two full blocking GCs after build and warmup, before measuring; tiered compilation on
  nosettle         no forced GC before measuring; tiered compilation on
  settle-notiered  as settle, with tiered compilation off

Within one configuration, scan1 runs first and its scan-window size is passed to baseline and owner, so all three
report window statistics over the same number of operations.
"""
import json
import os
from pathlib import Path
import subprocess
import sys

here = Path(__file__).resolve().parent
output = here / "results" / (sys.argv[2] if len(sys.argv) > 2 else "gc_diag_v2.jsonl")
if output.exists():
    raise SystemExit(f"Refusing to overwrite {output}")
dotnet = sys.argv[1] if len(sys.argv) > 1 else "/usr/local/share/dotnet/dotnet"
dll = here / "bin/Release/net10.0/MemberProtocol.dll"
base_env = {k: v for k, v in os.environ.items()
            if not (k.startswith(("DOTNET_", "COMPlus_")) and k != "DOTNET_ROOT") and k != "GCDIAG_SETTLE"}
base_env["DOTNET_gcServer"] = "0"
conditions = {
    "settle": {"GCDIAG_SETTLE": "1", "DOTNET_TieredCompilation": "1"},
    "nosettle": {"GCDIAG_SETTLE": "0", "DOTNET_TieredCompilation": "1"},
    "settle-notiered": {"GCDIAG_SETTLE": "1", "DOTNET_TieredCompilation": "0"},
}


def run(variant, count, length, window_ops, env):
    args = [dotnet, str(dll), "--gc-diag", variant, str(count), str(length)]
    if window_ops is not None:
        args.append(str(window_ops))
    result = subprocess.run(args, text=True, capture_output=True, check=True, timeout=120, env={**base_env, **env})
    return json.loads(result.stdout)


with output.open("x") as stream:
    for repeat in range(3):
        for condition, env in conditions.items():
            for count in (10000, 100000):
                for length in (16, 128):
                    scan = run("scan1", count, length, None, env)
                    window_ops = max(1, scan["window"]["ops"])
                    for record in (scan, run("baseline", count, length, window_ops, env), run("owner", count, length, window_ops, env)):
                        record.update(repeat=repeat + 1, condition=condition, windowOpsRequested=window_ops)
                        stream.write(json.dumps(record) + "\n")
                        stream.flush()
        print(f"Completed repeat {repeat + 1}", flush=True)
