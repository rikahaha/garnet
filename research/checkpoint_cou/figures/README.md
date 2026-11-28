# 论文用图：吞吐量

由 `make_throughput_figs.py` 生成（`python3 make_throughput_figs.py`），数据来自 `../delta/results_cygnus_churn/` 和
`../delta/results_cygnus_churn_hi/`（cygnus，GarnetServer `ckpt-delta`，改动列表 + 10% 门槛）。PDF 的字体以 TrueType 嵌入，可直接用于 LaTeX。
配色：原版为灰色参照（斜线 / 虚线作为第二编码，黑白打印也能区分），两种方法用蓝、橙（已通过色盲区分度检查）。

## throughput_timeline.pdf

建议图注：Throughput over time with six snapshot checkpoints (shaded). 20 sorted sets of 1M members, 16 clients, 50% ZINCRBY /
50% ZSCORE on uniformly random members; throughput per 100 ms. Unmodified Garnet drops to near zero at every checkpoint; with member
views throughput stays at the normal level. The dips at 17 s (member views) and 29 s (views + deltas), outside checkpoints, are .NET
GC pauses.

说明：
- 每个配置单次运行（`churn_<config>_read50`）。阴影是 trace 中 `ckpt_prepare` 到 `ckpt_flushed` 的区间。
- 增量的第 1 次 checkpoint 写进快照，第 2 次建链（阴影较宽，约 2.5 s），第 3–6 次是差量（几十到几百毫秒，阴影很窄）。
- checkpoint 内外的尖峰下跌都与 GC 实际暂停吻合（见 `../delta/results_cygnus_listcmp/RESULTS.md`、`../delta/results_cygnus_gc/RESULTS.md`）。

## throughput_during_ckpt.pdf

建议图注：Throughput during checkpoints as a share of normal throughput, by write share and number of clients. Bars: mean of six
checkpoints; whiskers: minimum to maximum. Each checkpoint is measured from its start over its duration or 1 s, whichever is longer.

说明：
- "平时"= 第 0.5 秒到第一次 BGSAVE 之间的吞吐量；每次 checkpoint 从开始起取 max(时长, 1 s) 的窗口。设 1 s 下限，是因为改动很少时
  增量的 checkpoint 只有 20–100 ms，这么短的窗口测出的比例全是噪声（曾出现 100–169%）。因此图中数值与 RESULTS.md 表里按 checkpoint
  实际时长算的比例不完全相同（原版的值偏高，因为窗口包含了 checkpoint 结束后的恢复部分）。
- 每个点单次运行（6 次 checkpoint）。
- 64 / 128 个连接、写比例较高时，增量和视图偶尔低于 80%，对应 GC 暂停。
