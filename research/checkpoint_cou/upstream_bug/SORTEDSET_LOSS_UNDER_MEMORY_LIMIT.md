# 已澄清：`-m 8g` 下 SortedSet "丢数据"是缓存模式的正常淘汰，不是 Garnet 的 bug

记录日期 2026-11-27 / 28。发现于测量增量 checkpoint 的恢复时间时（`delta/recovery_time.py`）。

## 结论

我们的实验都没有加 `--storage-tier`。Garnet 默认 `EnableStorageTier = false`（`libs/host/defaults.conf`），这时主日志设备是
`NullDevice`（`GarnetServerOptions.cs` 中 `kvSettings.LogDevice = new NullDevice()`），Garnet 相当于一个**缓存**：超过 `-m`
的内存上限时，被换出的记录直接被丢弃。被丢弃的是整个 key（一个 SortedSet 是一条记录），之后负载里的 ZINCRBY 又新建了这个集合，
所以看到"只剩几千到一万多个成员"——那是淘汰后被重新写到的不同成员数。

验证（完全没改过的 `main`，`66172eb`，同样 `-m 8g`、同样负载、第 1/4/7 秒 BGSAVE，`diag_loss.py`）：

| 主日志 | 是否发生换出 | 集合变小 |
| --- | --- | --- |
| 默认（NullDevice，缓存模式） | 是（第 3 次 BGSAVE 时整个日志换出） | 20 个中 11–14 个 |
| `--storage-tier --logdir ...`（真实日志设备） | 是（HeadAddress 64 → 1984） | **0 个**，换出后从磁盘读回，全部 100 万成员 |

感谢外部审阅指出这一点。最初的推测（"某条记录在磁盘上被写成空对象"）是错的，已删去。

## 仍然成立的观察

- `-m` 包含对象堆内存。灌完 20 × 100 万成员时 `Log.CurrentHeapSizeBytes` = 3.2 GB；每做一次写入负载下的 checkpoint 增加约 3.2 GB
  （存储层模式下实测 3.2 → 6.4 GB；缓存模式下 2 次后 9.6 GB）。这是 `INFO STORE` 的实测值；具体是哪些对象或缓冲在记账，
  没有从源码确认（copy-update 的克隆与旧对象共用内部集合，旧版本也有序列化字节，二者都可能计入）。
- 因此在 `-m 8g` 下，大集合 + 连续 checkpoint 会很快触发换出。

## 对本研究的影响

1. **所有之前的实验都运行在缓存模式（NullDevice）下。** 对内存中的吞吐量和停顿测量这是可以的，但要在论文里写明配置；
   并且内存上限必须足够大，否则会测到"淘汰 + 重建"的行为。之后用大 `-m`（例如 32g；`-m` 是上限，不是预先分配）并在运行结束时检查集合大小。
2. 之前 `-m 8g`、100 万成员、2 次 checkpoint 的吞吐量实验：成员模式的 trace 显示两次 checkpoint 时集合都完整；
   原版配置没有记录集合大小，无法确认是否发生淘汰。"池化后第 2 次 checkpoint 只要约 0.9 秒"标为待验证，用大 `-m` 重测。
3. **增量 checkpoint 的"存根绝不进入主日志"这一保护，在缓存模式下根本不会被用到**（主日志不写盘）。必须在 `--storage-tier` 下补测
   `delta_correctness.py` 的各个场景。

## 复现

```
# 原版：git worktree add --detach <dir> main && dotnet build <dir>/main/GarnetServer/GarnetServer.csproj -c Release
GARNET_DLL=<dir>/main/GarnetServer/bin/Release/net10.0/GarnetServer.dll python3 research/checkpoint_cou/delta/diag_loss.py
STORAGE_TIER=1 GARNET_DLL=... python3 research/checkpoint_cou/delta/diag_loss.py
# MEM（默认 8g）、SAVEAT（默认 1,4,7）、MEMBERS（默认 1000000）可用环境变量改
```
