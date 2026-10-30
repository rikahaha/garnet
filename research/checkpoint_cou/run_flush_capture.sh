#!/bin/bash
# Flush capture alone vs baseline: does releasing the object before the flush's device write (and sharing the capture with
# CopyUpdate) shorten request stalls? Two consecutive checkpoints per run (at 2 s and 5 s), two set sizes, REPS repetitions.
# Optional: GARNET_BIN / GARNET_NUMA (see object_cou_latency.py) and CLIENT_NUMA, e.g. "numactl --cpunodebind=0 --membind=0".
# Per-request latencies go to flush_capture/lat_<config>_m<members>_r<rep>.csv, traces to flush_capture/trace_*.csv,
# and the printed summaries (including server RSS) to flush_capture/summary_*.txt.
set -e
cd "$(dirname "$0")"
mkdir -p flush_capture
REPS=${REPS:-3}
for rep in $(seq 1 "$REPS"); do
  for m in 100000 1000000; do
    for config in baseline flushcapture; do
      case $config in
        baseline)     capture=0 ;;
        flushcapture) capture=1 ;;
      esac
      echo "### config=$config members=$m rep=$rep"
      TSAVORITE_FLUSH_CAPTURE=$capture CHECKPOINT_TIMES=2,5 MODE=snapshot \
        $CLIENT_NUMA python3 -I object_cou_latency.py "$m" 20 > "flush_capture/summary_${config}_m${m}_r${rep}.txt"
      grep -E "ckpt|RSS" "flush_capture/summary_${config}_m${m}_r${rep}.txt"
      cp "lat_snapshot_m${m}_s20.csv" "flush_capture/lat_${config}_m${m}_r${rep}.csv"
      cp work/trace.csv "flush_capture/trace_${config}_m${m}_r${rep}.csv"
    done
  done
done
