# 多客户端吞吐量：原始 Garnet vs 快照视图（cygnus）

2026-11-25，cygnus。GarnetServer 为 `4ba0fb2`（之后 Garnet 代码未改）的 linux-x64 自包含版本；压测程序 `ThroughputBench`。
server `numactl --cpunodebind=1 --membind=1`，客户端 `numactl --cpunodebind=0 --membind=0`；开始前两个节点 CPU 忙碌 0%。
运行后已删除服务器上的 `~/member-tput/`。

设置：20 个 SortedSet（10 万或 100 万成员）；16 个连接，每个连接发一条、等回复再发下一条（pipeline 1）；
每条命令随机选集合和已有成员，ZSCORE 与 ZINCRBY 各半；运行 20 秒，第 5、12 秒各发一次 BGSAVE；每组 3 次（每组 6 次 checkpoint）。
配置：`baseline`（开关关闭）、`view`（`GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`）、`viewpooled`（再加 `TSAVORITE_SERIALIZE_POOLED=1`）。

**负载说明**：事先的连接数扫描（无 checkpoint，10 万成员）中吞吐量随连接数近似线性增长
（4 / 8 / 16 / 32 个连接：117 / 230 / 429 / 828 k ops/s），所以 16 个连接时服务器**没有饱和**。
以下是中等负载下的结果，不代表服务器满载时的吞吐量。

原始数据：`*_r*.json`（每 10 ms 完成数、每 100 ms 延迟直方图）、`*_trace.csv`（checkpoint 起止）、`*_server.log`、`run.log`。
汇总：`throughput_analyze.py`。

## checkpoint 期间的吞吐量（k ops/s）

平时（第一次 BGSAVE 前，以及最后一次 checkpoint 结束 1 秒后）三种配置都是 431–446 k ops/s，看不出差别。

| 成员数 | 配置 | checkpoint 期间（6 次） | 平均占平时的比例 | checkpoint 时长 ms |
| --- | --- | --- | --- | --- |
| 10 万 | baseline | 269、41、255、26、207、56 | 约 32% | 115–367 |
| 10 万 | view | 252、455、250、405、181、447 | 约 76% | 170–622 |
| 10 万 | viewpooled | 346、452、446、454、345、457 | 约 95% | 323–659 |
| 100 万 | baseline | 146、135、188、173、197、197 | 约 39% | 664–861 |
| 100 万 | view | 427、422、415、388、410、400 | 约 94% | 1684–1860 |
| 100 万 | viewpooled | 410、449、420、451、375、446 | 约 97% | 899–1975 |

## checkpoint 期间的延迟

| 成员数 | 配置 | p99.9（µs，6 次） | 最大（ms，6 次） |
| --- | --- | --- | --- |
| 10 万 | baseline | 10111、188858、332、134218、11147、41617 | 29、189、34、161、33、172 |
| 10 万 | view | 366、385、248、260、108、93 | 166、23、150、20、378、1 |
| 10 万 | viewpooled | 93、63、349、332、63、81 | 150、1、5、1、158、1 |
| 100 万 | baseline | 385、2986、316、385、349、349 | 140、384、287、262、154、306 |
| 100 万 | view | 316、316、60、316、73、70 | 76、87、143、227、149、180 |
| 100 万 | viewpooled | 70、63、332、316、60、93 | 88、1、98、0、168、2 |

（p99.9 来自 5% 精度的直方图；最大值精确。）

## 观察

1. **checkpoint 期间吞吐量**：baseline 平均只剩平时的约 1/3；view 在 100 万成员时保持约 94%，viewpooled 在两种大小下都保持约 95–97%。
2. **10 万成员时 baseline 的 p99.9 达到 42–189 ms**（整对象序列化挡住了请求），view / viewpooled 都在 0.4 ms 以下。
3. **池化**（viewpooled）进一步提高了 10 万成员时的 checkpoint 期间吞吐量（76% → 95%），并且多次 checkpoint 的最大延迟降到 0–5 ms。
4. **仍有 100 ms 以上、全部连接同时停住的停顿**：每 100 ms 完成数为 0 的窗口在三种配置中都出现过，
   **而且出现在没有 checkpoint 的平时**（平时最大延迟：baseline 2–539 ms，view 110–643 ms，viewpooled 2–191 ms）。
   所有连接同时停住，像是整个进程暂停（例如 GC），但这次没有记录 GC 事件，**原因未确认**。
   因此表中 view / viewpooled 的 100–378 ms 最大值，不能直接归因于 checkpoint。
5. **代价**：checkpoint 持续时间变长（100 万成员：baseline 0.66–0.86 s，view 1.7–1.9 s），因为保存旧数据的工作移到了刷盘线程；
   viewpooled 第二次 checkpoint 约 0.9 s，可能是复用了第一次归还到池里的块。
6. 平时吞吐量三种配置相同，但服务器未饱和，平时的额外开销在这个负载下看不出来，需要饱和负载才能测出。

## 下一步

- 记录 server 的 GC 停顿（例如 `DOTNET_gcServer` 下的 EventPipe / `GC.GetTotalPauseDuration` 采样），确认全体停住的停顿是否来自 GC；
- 饱和负载（更多连接或 pipeline）下重测，得到平时开销和 checkpoint 期间吞吐量下降的数字。
