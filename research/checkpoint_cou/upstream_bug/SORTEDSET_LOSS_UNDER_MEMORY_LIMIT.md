# 原版 Garnet：内存上限 + 连续 checkpoint 时 SortedSet 在运行中丢数据

记录日期 2026-11-27 / 28。发现于测量增量 checkpoint 的恢复时间时（`delta/recovery_time.py`）。**原因尚未完全确定**，以下区分"已确认"和"推测"。
尚未报告给 Garnet 官方。

## 已确认

**完全没改过的 Garnet**（本仓库 `main`，`66172eb`，在单独的 worktree 编译，未改 main 分支）也会发生：

- 设置：`--device-type RandomAccess -m 8g -i 64m`，snapshot checkpoint（默认），无 AOF。
- 负载：20 个 SortedSet × 100 万成员（`ThroughputBench` 灌入），16 个连接，每条命令随机选集合和已有成员，ZSCORE / ZINCRBY 各半。
- 运行中第 1、4、7 秒各发一次 BGSAVE。
- 结果：第 3 次 BGSAVE 那一刻，20 个集合中有 11–14 个**在运行中**变成空集合，之后被 ZINCRBY 重新填进几千到一万多个成员。
  恢复（`--recover`）后的内容也就是错的。

| 条件 | 结果 |
| --- | --- |
| `-m 8g`，不做 checkpoint | 不丢（20 个集合都是 100 万） |
| `-m 8g`，3 次 checkpoint（间隔 3 秒） | 11–14 个集合丢数据（重复 3 次都发生） |
| `-m 32g`，3 次 checkpoint | 不丢 |
| `-m 8g`，2 次 checkpoint（第 5、12 秒，吞吐量实验的设置） | trace 显示两次 checkpoint 时所有集合都是完整的（只在成员模式下有 trace；原版配置没有记录） |

`diag_loss.py`（每 100 ms 记录每个集合的 ZCARD 和 `INFO STORE`）在原版 Garnet 上的输出：

```
MEM=8g SAVEAT=1,4,7: 14 of 20 sets shrank
  t=6.93s shrunk=[]   HeadAddress=64   FlushedUntilAddress=64   TailAddress=1984 CurrentHeapSizeBytes=9600007680
  t=7.04s shrunk=[14] HeadAddress=1984 FlushedUntilAddress=1984 TailAddress=2624 CurrentHeapSizeBytes=960386400
```

- 丢数据前，日志里没有任何记录被换出（HeadAddress = 64），但对象堆内存记账已经是 **9.6 GB**，超过 8 GB 上限。
- 第 3 次 BGSAVE 时，HeadAddress 一下跳到之前的尾部（1984），即**整个日志一次性换出到磁盘**，堆内存记账掉到 0.96 GB。
  数据丢失与这次换出发生在同一个 100 ms 窗口内。

**堆内存记账随 checkpoint 成倍增长**：灌完数据时 `CurrentHeapSizeBytes` = 3.2 GB，2 次 checkpoint 后 = 9.6 GB（3 倍）。
checkpoint 中的 copy-update 产生的克隆（`new SortedSetObject(this)`，与旧对象**共用**内部的 SortedSet 和字典）以完整的
`HeapMemorySize` 记账，旧版本在被换出之前一直不减。所以每做一次 checkpoint，同一份数据多记一次，第 3 次时超过上限，触发换出。

已排除：

- `SortedSetObject.Dispose()` 在原版里是空的，所以不是"换出旧记录时释放对象，把共用数据清空"。
- 只灌数据、不让日志增长时，即使堆内存记账（3.2 GB）超过上限（2 GB），也不会换出（没有触发点）。

## 推测（未确认）

换出时所有页面先刷到主日志，再从内存中移走。之后访问这些集合要从磁盘读回记录。如果某个集合**当前有效的记录**在磁盘上写的是
空对象（或没有对象），Garnet 就会当作集合不存在，后续 ZINCRBY 新建空集合——与现象一致。

可能写出空对象的地方：被 copy-update 取代的对象处于 SERIALIZED 状态，在 checkpoint 结束后释放了缓存字节，之后刷盘写 null
（`HeapObjectBase.Serialize` 中"已被取代则写 null"的分支），或者非 checkpoint 路径的 copy-update 把源对象标成"SERIALIZED、
无字节"并清掉对象引用（`CacheSerializedObjectData` 中 `ClearSourceValueObject`）。这些在源对象确实已被取代时是对的；
如果在 checkpoint 版本切换 + 强制换出的交错中，某个仍是最新的记录被这样处理，就会丢数据。具体是哪条路径还没定位。

## 未完成的诊断

- 不做 checkpoint、只让日志增长来强制换出（`-m 2g`，灌数据后写 10 万个 1 KB 字符串）：运行超过 10 分钟没有结束，已中止。
  是换出大对象本身很慢还是卡住，未知。
- 需要：在出事的那一刻抓取某个丢失集合的记录链（地址、版本、对象状态），确定是哪条路径写出了空对象。

## 对本研究的影响

- 之前 `-m 8g` 下的实验（吞吐量实验：100 万成员、2 次 checkpoint）：成员模式的 trace 表明两次 checkpoint 时集合都完整；
  原版配置没有记录集合大小，无法确认。"池化后第 2 次 checkpoint 只要约 0.9 秒"需要在大内存上限下重测确认。
- 以后的实验：使用足够大的 `-m`（例如 32g，`-m` 是上限，不是预先分配），并在每次运行结束时检查集合大小。
- 这个问题本身（大对象 + checkpoint 的克隆记账 + 内存上限）可以作为论文中的一个观察。

## 复现

```
# 编译原版：git worktree add --detach <dir> main && dotnet build <dir>/main/GarnetServer/GarnetServer.csproj -c Release
GARNET_DLL=<dir>/main/GarnetServer/bin/Release/net10.0/GarnetServer.dll python3 research/checkpoint_cou/delta/diag_loss.py
# MEM（默认 8g）、SAVEAT（默认 1,4,7）、MEMBERS（默认 1000000）可用环境变量改
```
