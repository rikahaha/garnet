# 成员级增量 checkpoint：设计（方案 B）

分支 `ckpt-delta`。第 1 步（差量大小估计）见 `results_local/RESULTS.md`。本文是第 2 步（真正写差量、恢复）的设计；实现状态见文末。

## 目标

大 SortedSet 在快照 checkpoint 中不再每次整个写出：快照文件里只写一个**存根**（集合 id + 序号），成员数据由 Garnet 写到
**侧存储**（side store）：每个集合一条"完整版本 + 差量"的链。恢复时按链读回。快照视图（不卡请求）保持不变，增量叠在它上面。

## 调查得到的约束（代码事实）

1. 同一条记录在 checkpoint 期间可能写两次：写进快照文件，也可能写进主日志（只读区推进时）。被取代的旧记录会把第一次读出的字节
   保留下来给第二次写用（`FlushFromView` 的 kept 分支）。
2. **主日志里的对象在恢复时会被反序列化**，包括已被取代、不可达的旧记录（`DeserializeObjectsOnPage` 遍历所有有效记录）。
   所以主日志里**绝不能出现存根**，否则差量文件被清理后恢复会失败。
3. 刷盘代码能区分写入目标：`ObjectAllocatorImpl.WriteAsync` 里 `asyncResult.flushRequestState == FlushRequestState.Snapshot`。
4. 恢复时，快照区的页面只有在**恢复过程中因内存预算被换出**时，才会把对象字节原样复制进主日志（`isSnapshotRecoveryCopy`）。
   没有设置堆内存上限（没有 `logSizeTracker`）时不会发生。
5. Garnet 固定 `removeOutdated: true`，`logTokenCount = 1`：只保留最新一个 log checkpoint；恢复时删掉其他所有 log checkpoint。

## 设计

### 1. 写入目标标记（Tsavorite，小改动）

- `WriteAsync` 在 Snapshot 刷盘调用 `WriteRecordObjects` 前后设置一个线程局部标记 `HeapObjectBase.InSnapshotFlush`。
- `WriteSnapshotView(writer)` 由此知道这次是不是写快照文件。只有写快照文件时才可以写存根；写主日志一律写完整格式（和现在一样）。
- kept 字节带一个"只能用于快照"标记。被取代的对象在之后的主日志刷盘中遇到这种字节时**写空对象**（null）。这和 checkpoint 结束后
  被取代对象写 null 的现有行为一致；被取代的记录不可达，本次 checkpoint 的恢复用的是快照文件里的那份。

### 2. 侧存储（Garnet，新文件 `SortedSetSideStore`）

- 目录：`<checkpointdir>/sortedset-members/<uid>/<seq>.full|.delta`。`uid` 是成员存储创建时生成的随机 64 位 id（DEL 后重建是新对象、
  新 uid），`seq` 是这个集合的写出序号。
- 文件内容：魔数、uid、seq、类型、baseSeq（差量才有）、成员行（key 长度、key、score）、被删 key、末尾校验和。
- **写入在刷盘线程上同步完成并 fsync，然后才往快照里写存根**。快照刷盘完成之前 checkpoint 不会提交，所以"存根已提交 ⇒ 它引用的
  文件已落盘"自动成立。
- 链规则：满足以下任一条件就写完整版本：没有 base；链里已有 4 个差量；估计差量超过完整大小的 25%；集合小于阈值（例如 1 万成员，
  小集合照旧把成员直接写进快照，不产生文件）。
- 差量大小在读视图**之前**决定（读完视图就不能回头写完整版本了）：成员存储按 epoch 记录"这一轮第一次被改的成员数"（第一步已有
  epoch，只需加计数器）。base 是上一轮时这个数是精确的。

### 3. base 何时前进

- 差量文件 fsync 后，这个集合的 base 就前进到这次的 seq。base 的有效性只取决于**侧存储文件是否落盘**，不取决于 checkpoint 是否
  成功：即使这次 checkpoint 中止，文件仍在，下一次差量引用它没有问题（清理规则保证它不会被删，见下）。
- 恢复后，新建的成员存储没有 base，第一次 checkpoint 写完整版本（以后可优化为沿用恢复时的链）。

### 4. 恢复

- `SortedSetObject(BinaryReader)` 读到存根标记（成员数为负）后，按 uid、seq 从侧存储读链：从完整版本开始，依次应用差量
  （按 key 覆盖分数，删除被删 key），校验每个文件的 uid、seq、baseSeq 和校验和。
- 文件缺失或校验失败：抛异常，恢复失败，不静默恢复出错误数据。
- 恢复期间记录"被引用的链"，恢复完成后清理其余文件。

### 5. 清理

- 每次 checkpoint 刷快照时记录本次写出的存根引用了哪些链（uid → 链起点 seq）。
- checkpoint **完成**（元数据落盘，Garnet 的 checkpoint 等待返回）后：删除不在这份清单里的所有文件和目录。
  因为只保留最新一个 checkpoint，这就够了；进行中的 checkpoint 写的文件序号更大，清理时保留比清单更新的文件。
- 不在清单里的集合要么已删除，要么它的记录在主日志区（主日志里是完整数据），都不再需要侧存储。

### 6. 先不支持（回退成现在的完整写入）

- 带成员 TTL 的集合（本来就没有视图）。
- 集群模式（副本同步靠复制 checkpoint 文件，拿不到侧存储）：打开集群时不写存根。
- 多数据库：先只支持 DB 0。
- 恢复时因内存预算换出快照页（约束 4）：存根会被复制进主日志。先作为前提写明（不设堆内存上限），运行时如果在恢复以外读到存根，
  按"文件缺失"同样抛异常，保证不会静默出错。

## 开关

`GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`（视图）之外，再加 `GARNET_SORTEDSET_DELTA=1` 打开存根和侧存储。都关掉就是原来的 Garnet。

## 测试清单

| # | 场景 | 检查 |
| --- | --- | --- |
| 1 | 单元：链的写读——完整 + 若干差量（改分数、新增、删除、删了又加），读回与内存状态一致 | 逐成员相等 |
| 2 | 单元：链长上限、超过 25% 改写完整版本、小集合不产生文件 | 文件类型和数量 |
| 3 | 单元：文件缺失、截断、校验和错误、uid/baseSeq 不符 | 抛异常 |
| 4 | 端到端（场景 C 扩展）：连续 6 次以上 checkpoint 形成链并跨过上限，期间持续写入，kill -9，无 AOF 恢复 | 每个集合等于某个前缀状态，全局前缀一致 |
| 5 | 端到端：两次 checkpoint 之间 DEL 并重建集合 | 恢复出重建后的内容，旧 uid 文件被清理 |
| 6 | 端到端：checkpoint 写完差量后 kill -9（下一个 checkpoint 还没完成） | 用上一个完成的 checkpoint 正确恢复；多余文件在恢复后被清理 |
| 7 | 端到端：恢复后再写、再 checkpoint、再恢复（恢复后第一次写完整版本） | 内容正确 |
| 8 | 端到端：有 AOF 时 checkpoint + AOF 重放 | 与崩溃前完全一致 |
| 9 | 清理：多次 checkpoint 后侧存储目录只剩最新 checkpoint 引用的链 | 文件列表 |
| 10 | 回归：成员模式和增量模式下 SortedSet 测试全过；开关全关时 Garnet 全部相关测试通过 | 测试结果 |

## 实验（实现并通过测试后）

- 本地：改动比例 0.1%–50% 和 Zipf 负载下，checkpoint 写出字节数、checkpoint 时长、恢复时间（随链长）、侧存储占用。
- cygnus（照例先给计划）：吞吐量实验加上增量配置，对比 checkpoint 时长和 checkpoint 期间吞吐量。

## 实现状态（2026-11-27）

已实现，开关 `GARNET_SORTEDSET_DELTA=1`（需同时打开 `GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`），阈值 `GARNET_SORTEDSET_DELTA_MIN`（默认 10000）。

| 位置 | 内容 |
| --- | --- |
| `Tsavorite/.../ObjectAllocatorImpl.cs` | Snapshot 刷盘序列化对象时设置 `HeapObjectBase.InSnapshotFlush` |
| `Tsavorite/.../HeapObjectBase.cs` | `WriteSnapshotView` 返回"是否只对快照有效"；这种 kept 字节在其他刷盘中写 null |
| `server/Objects/SortedSet/SortedSetSideStore.cs`（新） | 链文件格式、写入并 fsync、按链读回并校验、清理 |
| `server/Objects/SortedSet/SortedSetMemberStore.cs` | 存储 uid、链状态（BaseSeq / ChainStart / ChainDeltas）、每轮被改成员计数、被删 key（有上限，超出则丢弃 base） |
| `server/Objects/SortedSet/SortedSetObject.cs` | 写快照：大集合写链文件 + 存根，其余内联；构造时读到存根就从侧存储读回 |
| `server/StoreWrapper.cs`、`Databases/DatabaseManagerBase.cs` | 配置目录；checkpoint 完成后、恢复后清理；其他数据库做 checkpoint 时停止写存根 |

与设计的差异：

- 多数据库：Garnet 默认允许多个数据库，所以不是"有多数据库就不写"，而是"一旦有非 0 号数据库做 checkpoint，就停止写存根"。
- 小集合写进快照时（内联），链状态清零，下一次链文件必须是完整版本。

测试：

- 单元（`test/standalone/Garnet.test.collections/SortedSetSideStoreTests.cs`，7 个）：链的写读、删了又加、缺文件 / 损坏 / 截断 / 别的 uid 的文件都抛异常、未提交的文件被删、清理规则。
- 端到端（`delta_correctness.py`）：chain（7 次 checkpoint，写入不停，kill -9，前缀一致；链在 4 个差量后被完整文件截断；清理后每个集合只剩当前链）、
  restart（DEL 后重建、恢复后第一次写完整、再写差量、再恢复）、crash（checkpoint 进行中 kill -9 共 8 次，恢复结果总是上一次或进行中那一次）、
  aof（checkpoint + AOF 重放）。全部通过。
- 回归：成员 + 增量模式、阈值 1（所有 SortedSet 都走侧存储）下 `Garnet.test.collections` 的 SortedSet 测试 358 个全部通过。
- 回归：`Garnet.test.collections` 全部 853 个，增量开（阈值 1）和全关两种模式都通过。`Garnet.test`（排除 `MultiTcpSocketTest`）两种模式都是
  1533 通过、同样 4 个失败（`PinnedBasePortThatIsOccupiedFails`、`ListenerIsDetectedOnEveryBoundAddressKind` ×3，本机端口占用检测相关，与开关无关）。
  `MultiTcpSocketTest` 在本机连 IPv6 链路本地地址失败，且失败后不关闭服务器，连带后续测试绑不上端口，所以单独排除。

## 修正（2026-11-29，外部审阅）

- 链文件编号改为每个成员存储单调递增（`AllocateSeq`），丢弃基版本后也不复用；创建文件用 `FileMode.CreateNew`。回归测试
  `DroppedBaseDoesNotOverwriteTheLastCompletedChain`（上一次完成的链 → 内联重置 → 新链写一半未提交 → 恢复上一次，逐成员核对）和
  `ExistingChainFileIsNeverOverwritten`；两者在旧行为下都失败。
- 打开视图不再复制被删 key（O(1)）；写链文件时 `ReadDeletedKeys` 按位置和轮次读取；视图打开期间 `DropBase` 推迟清空列表。
- 已知限制：侧存储是进程内静态状态，同一进程的多个实例会互相干扰。
