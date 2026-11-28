# cygnus：原版 vs 快照视图 vs 增量（2026-11-28）

GarnetServer 为 `ckpt-delta` 的 `75deedd`（linux-x64 自包含）。`run_delta_server.sh`。server `numactl --cpunodebind=1 --membind=1`，
客户端 `--cpunodebind=0 --membind=0`；开始前负载 0.2。`-m 32g`（缓存模式，无 `--storage-tier`；上限足够大，不会淘汰）。
运行后已删除服务器上的 `~/member-delta/`。

设置：20 个 SortedSet（10 万或 100 万成员），16 个连接，pipeline 1，ZSCORE / ZINCRBY 各半，随机集合和已有成员；20 秒，第 5、12 秒 BGSAVE；
每组 3 次。配置：`baseline`（全关）、`viewpooled`（`GARNET_SORTEDSET_MEMBER_CHECKPOINT=1 TSAVORITE_SERIALIZE_POOLED=1`）、
`delta`（再加 `GARNET_SORTEDSET_DELTA=1`，阈值 1 万成员）。

**淘汰检查**：每次运行结束时记录每个集合的 ZCARD（`throughput/*_sizes.txt`），18 次全部等于预装成员数。

## 100 万成员

| | baseline | viewpooled | delta |
| --- | --- | --- | --- |
| 第 1 次 checkpoint 时长 ms | 801、729、589 | 1724、1986、1717 | 2237、2369、2273（写完整链文件） |
| **第 2 次 checkpoint 时长 ms** | 1055、777、668 | 1024、1046、1096 | **571、561、585**（写差量） |
| 第 1 次期间吞吐量 / 平时 | 41%、51%、41% | 103%、90%、99% | 100%、101%、103% |
| 第 2 次期间吞吐量 / 平时 | 24%、37%、38% | 104%、103%、102% | 101%、101%、103% |
| 第 1 次期间最大延迟 ms | 143、152、166 | 4、231、4 | 5、6、26 |
| 第 2 次期间最大延迟 ms | 520、323、298 | 1、1、1 | 2、1、2 |
| 第 2 次写出 | 全部成员（约 380 MB） | 同左 | 差量合计 28–30 MB（完整 398 MB） |

平时吞吐量三种配置都是 434–450 k ops/s。

## 10 万成员

| | baseline | viewpooled | delta |
| --- | --- | --- | --- |
| 第 1 次 checkpoint 时长 ms | 134、118、117 | 556、660、503 | 1062、940、742 |
| 第 2 次 checkpoint 时长 ms | 372、258、502 | 316、319、503 | 202、210、201 |
| 第 1 次期间吞吐量 / 平时 | 54%、45%、59% | 102%、77%、103% | 89%、83%、104% |
| 第 2 次期间吞吐量 / 平时 | 7%、10%、5% | 102%、101%、108% | 106%、108%、110% |
| 第 2 次期间最大延迟 ms | 186、189、460 | 1、1、1 | 1、1、1 |

10 万成员时两次 checkpoint 之间改动超过 25%，delta 两次都写完整链文件（每次 37.8 MB），没有差量。

## 恢复时间（`delta/recovery_time.py`，100 万成员 × 20，单次）

| 配置 | 恢复时间 | 侧存储 |
| --- | --- | --- |
| baseline | 7.36 s | — |
| delta，0 个差量 | 11.32 s | 398 MB |
| delta，1 个差量 | 11.46 s | 411 MB |
| delta，2 个差量 | 11.49 s | 424 MB |
| delta，3 个差量 | 11.68 s | 437 MB |
| delta，4 个差量 | 11.84 s | 449 MB |

每多一个差量约 +0.1–0.2 s；但 delta 本身比 baseline 慢约 4 s（读链文件的实现慢，待优化）。

## 观察

1. **增量把第 2 次 checkpoint 缩短到 0.56–0.59 s**，比 viewpooled（1.0–1.1 s）快约一半，也比 baseline（0.67–1.06 s）短；
   checkpoint 期间吞吐量保持平时水平。
2. 代价：第一次 checkpoint 更长（2.2–2.4 s，写完整链文件 + fsync），恢复慢约 4 s。
3. viewpooled 第 2 次 checkpoint 比第 1 次短（约 1.0 s vs 1.7–2.0 s），在无淘汰的条件下仍然成立，之前"可能来自淘汰"的疑问排除。
4. 第 1 次 checkpoint 期间偶尔仍有 150–230 ms 的最大延迟（viewpooled 和 delta 都有，3 次中 1–2 次），原因同之前的全进程停顿，未归因。
