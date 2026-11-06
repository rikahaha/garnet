"""Sustained churn: GC cost of the Owner's per-member objects, workstation vs server GC. Preserves old result files.

usage: run_churn.py <dotnet-or-empty> <output-name> [--cpus N]

Every run sets its environment explicitly (inherited DOTNET_/COMPlus_ settings are removed):
  DOTNET_TieredCompilation=1, DOTNET_gcServer=0|1, DOTNET_gcConcurrent=1
  server GC additionally DOTNET_GCHeapCount=<cpus> and DOTNET_GCNoAffinitize=1, so both modes use the same CPU set.
Optional:
  CHURN_PREFIX   command prefix applied to every run, e.g. "numactl --physcpubind=1,3,5,7 --membind=1";
                 recorded in each result as environment.numa.
  CHURN_BIN      path to a self-contained MemberProtocol executable (otherwise <dotnet> <dll>).
  CHURN_OPS / CHURN_WARMUP / CHURN_REPEATS / CHURN_COUNTS   scale; defaults 1000000 / 200000 / 3 / 10000,100000.
  CHURN_LENGTHS / CHURN_VARIANTS / CHURN_GC_MODES   subsets; defaults 16,128 / baseline,owner,scan1 / workstation,server.
"""
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys

here = Path(__file__).resolve().parent
output = here / "results" / sys.argv[2]
if output.exists():
    raise SystemExit(f"Refusing to overwrite {output}")
cpus = int(sys.argv[sys.argv.index("--cpus") + 1]) if "--cpus" in sys.argv else os.cpu_count()
prefix = shlex.split(os.environ.get("CHURN_PREFIX", ""))
if os.environ.get("CHURN_BIN"):
    program = [os.environ["CHURN_BIN"]]
else:
    program = [sys.argv[1] or "/usr/local/share/dotnet/dotnet", str(here / "bin/Release/net10.0/MemberProtocol.dll")]
ops = os.environ.get("CHURN_OPS", "1000000")
warmup = os.environ.get("CHURN_WARMUP", "200000")
repeats = int(os.environ.get("CHURN_REPEATS", "3"))
counts = [int(c) for c in os.environ.get("CHURN_COUNTS", "10000,100000").split(",")]
lengths = [int(n) for n in os.environ.get("CHURN_LENGTHS", "16,128").split(",")]
variants = os.environ.get("CHURN_VARIANTS", "baseline,owner,scan1").split(",")

base_env = {k: v for k, v in os.environ.items()
            if not (k.startswith(("DOTNET_", "COMPlus_")) and k != "DOTNET_ROOT")}
base_env.update(DOTNET_TieredCompilation="1", DOTNET_gcConcurrent="1",
                CHURN_NUMA=os.environ.get("CHURN_PREFIX", "(none)"))
gc_modes = {
    "workstation": {"DOTNET_gcServer": "0"},
    "server": {"DOTNET_gcServer": "1", "DOTNET_GCHeapCount": str(cpus), "DOTNET_GCNoAffinitize": "1"},
}
selected_modes = os.environ.get("CHURN_GC_MODES", "workstation,server").split(",")
gc_modes = {mode: gc_modes[mode] for mode in selected_modes}

with output.open("x") as stream:
    for repeat in range(repeats):
        for mode, env in gc_modes.items():
            for count in counts:
                for length in lengths:
                    for variant in variants:
                        args = [*prefix, *program, "--churn", variant, str(count), str(length), ops, warmup]
                        result = subprocess.run(args, text=True, capture_output=True, check=True, timeout=600,
                                                env={**base_env, **env})
                        record = json.loads(result.stdout)
                        record.update(repeat=repeat + 1, gcMode=mode, cpusRequested=cpus)
                        stream.write(json.dumps(record) + "\n")
                        stream.flush()
        print(f"Completed repeat {repeat + 1}", flush=True)
