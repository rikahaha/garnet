#!/bin/bash
# Snapshot views (GARNET_SORTEDSET_MEMBER_CHECKPOINT=1) vs baseline: request latency of ZADD on large sorted sets while a
# snapshot checkpoint is in progress. Two consecutive checkpoints per run (at 2 s and 5 s), two set sizes, REPS repetitions.
# Optional: GARNET_BIN / GARNET_NUMA (see object_cou_latency.py) and CLIENT_NUMA, e.g. "numactl --cpunodebind=0 --membind=0".
# Per-request latencies go to member_ckpt/lat_<config>_m<members>_r<rep>.csv, traces to member_ckpt/trace_*.csv,
# and the printed summaries (including server RSS) to member_ckpt/summary_*.txt.
set -e
cd "$(dirname "$0")"
mkdir -p member_ckpt
REPS=${REPS:-3}
for rep in $(seq 1 "$REPS"); do
  for m in 100000 1000000; do
    for config in baseline view; do
      case $config in
        baseline) member=0 ;;
        view)     member=1 ;;
      esac
      echo "### config=$config members=$m rep=$rep"
      GARNET_SORTEDSET_MEMBER_CHECKPOINT=$member CHECKPOINT_TIMES=2,5 MODE=snapshot \
        $CLIENT_NUMA python3 -I object_cou_latency.py "$m" 20 > "member_ckpt/summary_${config}_m${m}_r${rep}.txt"
      grep -E "ckpt|RSS" "member_ckpt/summary_${config}_m${m}_r${rep}.txt"
      cp "lat_snapshot_m${m}_s20.csv" "member_ckpt/lat_${config}_m${m}_r${rep}.csv"
      cp work/trace.csv "member_ckpt/trace_${config}_m${m}_r${rep}.csv"
    done
  done
done
