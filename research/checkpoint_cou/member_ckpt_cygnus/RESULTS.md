# 快照视图接入 Garnet 后的第一次 cygnus 测量

2026-11-10，cygnus。程序为本地 `9cf889d` 发布的 linux-x64 自包含版本，脚本 `run_member_ckpt.sh`。
server `numactl --cpunodebind=1 --membind=1`，客户端 `numactl --cpunodebind=0 --membind=0`（与之前的 baseline 测量相同），
开始前两个 NUMA 节点的 CPU 平均忙碌 3% / 1%。运行后已删除服务器上的 `~/member-ckpt/`。

设置：20 个 SortedSet，每个 10 万或 100 万成员；一个客户端连续 ZADD；在第 2 秒和第 5 秒各发一次 BGSAVE（snapshot checkpoint）。
`baseline` = 开关关闭（现有代码），`view` = `GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`。每组 3 次。
没有开启 `TSAVORITE_SERIALIZE_POOLED`，所以刷盘读视图时用 `MemoryStream` + `ToArray` 生成字节。

原始数据：`summary_*.txt`、`trace_*.csv`（每请求延迟 `lat_*.csv` 体积大，只保存在本地，未提交）。

## 请求线程上发生了什么（trace）

| | baseline | view |
| --- | --- | --- |
| 请求线程整对象序列化 `cou_serialize` | 每次运行 24–40 次；最长 36–63 ms（10 万）、112–610 ms（100 万） | **0 次** |
| 请求线程建立视图 `cou_view` | — | 每次运行 39 次，最长 0.2–0.4 ms |
| 刷盘读视图时 CopyUpdate 不等待 `cou_reuse` | — | 每次运行 1 次 |

## checkpoint 期间请求的最大延迟（ms）

统计窗口是**整个 checkpoint**（从 BGSAVE 到 `ckpt_flushed`）。注意 `summary_*.txt` 里的数字只统计 BGSAVE 后第一秒；
100 万成员时 checkpoint 持续 1.7–2.0 秒，第一秒之后的尖峰不在其中，所以以下表为准。

每格 6 个数字：r1 第 1、2 次 checkpoint，r2 第 1、2 次，r3 第 1、2 次。

| 成员数 | baseline | view |
| --- | --- | --- |
| 10 万 | 51.3、63.0、48.0、26.0、36.1、25.3 | 6.2、21.1、17.7、2.7、3.2、23.1 |
| 100 万 | 114.9、84.9、114.1、610.1、120.1、84.3 | 3.1、16.9、15.1、**189.2**、3.9、**160.9** |

- 100 万成员时，baseline 在 checkpoint 开始后的第一秒内只完成 7–23 个请求（被整对象序列化挡住），view 完成 1.5–2.3 万个。
- **view 中剩下的大尖峰与 GC 停顿同时发生、长度相近**：100 万 r2 的 189 ms 尖峰期间，同一次刷盘读视图（259 ms）内 GC 停顿 188 ms；
  r3 的 161 ms 对应 160 ms GC；10 万 r1、r3 的 21–23 ms 对应 21–23 ms GC。baseline 的 610 ms 中也有 552 ms GC。
  这是时间上的对应，不是因果证明；推断原因是刷盘读视图时用 `MemoryStream`/`ToArray` 为每个集合分配大数组（100 万成员约 37 MB）。
- 其余的 3–17 ms 尖峰中，有一部分发生在没有刷盘重叠的时刻（如 checkpoint 之后），来源未归因。

## 内存

server RSS（checkpoint 前 → 峰值，MB）：10 万 baseline 347–348 → 445–483，view 431–434 → 534–592；
100 万 baseline 2519–2570 → 3649–3954，view 3338–3369 → 5071–5254。
view 平时多约 85 MB（10 万 × 20）和约 800 MB（100 万 × 20），即每成员约 40 B，与原型的 +42 B 一致；
峰值更高，因为读出的字节在 checkpoint 结束前一直保留。

## 结论与下一步

1. 请求线程上的整对象序列化已经完全消除，建立视图最长 0.4 ms；100 万成员时，checkpoint 期间多数运行的最大延迟从 85–610 ms 降到 3–17 ms。
2. 仍有 2/6 次 checkpoint（100 万）出现 160–190 ms 的停顿，与刷盘期间的 GC 吻合。
3. 下一步：同一设置加 `TSAVORITE_SERIALIZE_POOLED=1`（刷盘字节改用 64 KB 池化块，不进大对象堆），看这些 GC 停顿是否消失；
   以及改为直接流式写出、不保留整份字节的方案（需要处理同一记录被刷两次的情况）。
