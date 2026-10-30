#!/bin/bash
# Ablation: baseline / pooled buffers / pre-serialization / both, for two set sizes, 3 repetitions.
# Optional: GARNET_BIN / GARNET_NUMA (see object_cou_latency.py) and CLIENT_NUMA, e.g. "numactl --cpunodebind=0 --membind=0".
# Per-request latencies go to ablation/lat_<config>_m<members>_r<rep>.csv, traces to ablation/trace_*.csv.
set -e
cd "$(dirname "$0")"
mkdir -p ablation
REPS=${REPS:-3}
for rep in $(seq 1 "$REPS"); do
  for m in 100000 1000000; do
    for config in baseline pooled preser both; do
      case $config in
        baseline) pooled=0; threads=0 ;;
        pooled)   pooled=1; threads=0 ;;
        preser)   pooled=0; threads=4 ;;
        both)     pooled=1; threads=4 ;;
      esac
      echo "### config=$config members=$m rep=$rep"
      TSAVORITE_SERIALIZE_POOLED=$pooled TSAVORITE_PRESERIALIZE_THREADS=$threads MODE=snapshot \
        $CLIENT_NUMA python3 -I object_cou_latency.py "$m" 20 | sed -n 2,4p
      cp "lat_snapshot_m${m}_s20.csv" "ablation/lat_${config}_m${m}_r${rep}.csv"
      cp work/trace.csv "ablation/trace_${config}_m${m}_r${rep}.csv"
    done
  done
done
