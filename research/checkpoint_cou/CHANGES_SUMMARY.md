# 大集合 checkpoint 改进：到目前为止的改动总结

更新于 2026-11-28。分支：`ckpt-cou`（成员级快照视图）、`ckpt-delta`（在其上加成员级增量 checkpoint）。`main` 未改动。

## 1. 要解决的问题

Garnet 用 CPR 做一致性 checkpoint：开始时版本从 v 切到 v+1，之后把 v 版本的数据写到磁盘。一个 SortedSet 在日志里只是一条记录，
所以 checkpoint 期间第一次修改一个大集合时，Garnet 必须先保住它的 v 版本，做法是在处理这个请求的线程上、持有记录锁的情况下，
把整个集合序列化一遍（`HeapObjectBase.CacheSerializedObjectData`）。100 万成员的集合要几十到几百毫秒，这个请求和后面所有访问
这个集合的请求都被挡住。实测原版 Garnet 在 checkpoint 期间吞吐量只剩平时的 1–56%，最慢的请求要等 0.1–1.5 秒。
写的比例越低反而越糟：写只占 1% 时，一次写入就触发整个集合的序列化，挡住访问它的所有读请求，吞吐量只剩 1–3%。

Garnet 原论文（VLDB 2026）说 checkpoint 是不阻塞的，指的是线程如何协调版本切换；论文的实验没有测"大对象持续更新时 checkpoint
期间的请求延迟"。我们研究的就是这一点。

核心思路：把 CPR 的版本边界从"一条记录"推进到"集合里的每个成员"。

## 2. 分支和开关

| 开关（环境变量） | 作用 | 分支 |
| --- | --- | --- |
| `GARNET_SORTEDSET_MEMBER_CHECKPOINT=1` | 成员存储 + 快照视图 | ckpt-cou |
| `TSAVORITE_SERIALIZE_POOLED=1` | 序列化写入池化的 64 KB 块（减少大数组分配和 GC） | ckpt-cou |
| `GARNET_SORTEDSET_DELTA=1` | 大集合写成链文件 + 快照里放存根（增量） | ckpt-delta |
| `GARNET_SORTEDSET_DELTA_MIN` | 走增量的最小成员数，默认 10000 | ckpt-delta |
| `GARNET_SORTEDSET_DELTA_RATIO` | 改动超过这个比例就直接写进快照，默认 0.10 | ckpt-delta |
| `GARNET_SORTEDSET_DELTA_LIST` | 差量从改动列表读（默认 1）；0 = 扫描全部成员（用于对比） | ckpt-delta |
| `TSAVORITE_COU_TRACE`、`GARNET_DELTA_TRACE`、`GARNET_GC_TRACE`、`GARNET_GC_EVENTS` | 研究用记录：checkpoint 阶段、链文件写入及分段计时、GC 采样、GC 事件 | 两者 |

所有开关都关掉时，行为和原版 Garnet 一样。

## 3. 第一阶段（ckpt-cou）：成员级快照视图

### 3.1 先试过、后来放弃或保留为对照的做法

- **checkpoint 前预先序列化大对象**（`0f34178`）：在后台线程提前把大对象序列化好，copy-update 时直接用。能减少一部分等待，
  但对象在预序列化之后被改过就要重做，停顿还在。
- **池化缓冲**（`b1cffc3`）：序列化写进复用的 64 KB 块，而不是一个大 MemoryStream。减少大对象堆分配和 GC，后来作为
  `TSAVORITE_SERIALIZE_POOLED` 保留。
- **刷盘时先捕获再写盘**（`01246dc`）：刷盘线程序列化完就释放对象，写盘不占着对象，并把结果给 copy-update 复用。
  能去掉"等写盘"的那部分，但 copy-update 自己序列化的那部分还在。
- **独立的成员级快照协议**（`7ed0463` 起）：先在 Garnet 外面单独实现和测试"按成员保留旧值"的数据结构，在 cygnus 上测了
  持续插入删除时的停顿、写者优先、批量复用、分段数组布局，再整合进 Garnet。

### 3.2 最终做法

1. **成员存储**（`SortedSetMemberStore`，`c5d5ec0`）：SortedSet 的所有成员写入都经过它。成员放在分段数组里（每段 4096 个，
   每个数组都小于大对象阈值），每个成员有一个只增不减的 id，按 id 串成链。
2. **快照视图**（`4ba0fb2`）：checkpoint 中第一次修改集合时，不再序列化整个集合，只记一个边界（当时最大的 id 和成员数），
   耗时约 2.5 µs，请求马上继续。边界以内的成员第一次被改时先记下旧分数，被删的成员暂时保留，所以不复制整个集合也能读出 v 版本。
3. **后台读取**：写快照的线程按批（每批 128 个成员）读视图，写出和原来完全相同的格式，所以快照格式和恢复代码都不变。
   读的时候写请求优先（写者在等时，读者先让一次）。
4. **Tsavorite 里新增的状态**：`VIEW_OPEN`、`VIEW_FLUSHING`、`VIEW_FLUSHING_SUPERSEDED`，以及登记表 `CheckpointSnapshotViews`
   （checkpoint 开始时启用，结束或中止时统一关闭所有视图，避免泄漏）。
5. **回退**：带成员过期时间的集合不用视图，走原来的整体序列化。

## 4. 第二阶段（ckpt-delta）：成员级增量 checkpoint

### 4.1 先估算能省多少（`e77842c`）

给每个成员记"最后一次在哪一轮被改"，checkpoint 时统计"完整写出"和"只写改动"各要多少字节，不改写入。本地结果：改 1% 的成员时差量
约为完整的 1%，热点负载下 0.6–24%。统计和脚本实际改动的成员逐个核对一致。

### 4.2 设计选择（`36b2dd9`，`delta/DESIGN.md`）

差量不能引用旧 checkpoint 的文件，因为 Garnet 只保留最新一个 checkpoint，恢复时还会删掉其他所有 checkpoint。所以选了方案 B：
成员数据由 Garnet 自己存在单独的目录里，快照里只放一个"存根"。

### 4.3 实现（`4ca1467`）

- **链文件**：`checkpoints/sortedset-members/<集合 id>/<序号>.m`。每个集合一条链：一份完整版本，后面跟最多 4 个差量
  （新增或改过的成员、被删成员的 key）。文件末尾有条目数和校验和（现在是 XxHash64）。
- **存根**：快照里这个集合的记录只写"集合 id + 序号 + 成员数"。
- **写入顺序**：链文件在刷盘线程上写完并 fsync，然后才写存根；checkpoint 要等快照写完才提交，所以已提交的存根引用的文件一定在盘上。
- **base 何时前进**：只看链文件有没有落盘，和 checkpoint 成不成功无关；checkpoint 中止了，文件还在，下一次照样能引用。
- **恢复**：读到存根就从侧存储读链，从完整版本开始依次叠加差量（先删再加）。缺文件、校验失败、链对不上都报错，不会静默恢复出错误数据。
- **清理**：checkpoint 完成后、恢复完成后，删除最新 checkpoint 用不到的链。
- **存根绝不进主日志**：主日志里的对象以后可能被反序列化，链文件却会被清理。Tsavorite 的刷盘代码在写快照时打一个线程标记
  （`HeapObjectBase.InSnapshotFlush`），只有这时才写存根；写主日志一律写完整数据。被取代的旧对象如果手里只有"只对快照有效"的字节，
  写主日志时写空对象（它已不可达）。
- **回退**：成员过期时间、集群模式、非 0 号数据库（一旦有其他数据库做 checkpoint 就停止写存根）。

### 4.4 改动列表（`2f5ed5a`）

最初版本要找出改过的成员，得把每个集合的全部成员扫一遍。改成：成员在每一轮第一次被改（或新加入）时，往改动列表里记一条
（轮次、槽位、成员 id）；写差量时只读这张列表，用成员 id 防止槽位被复用后误读。关闭视图时也不再走完全部成员，
只处理一张"保留了旧值的槽位"清单。

### 4.5 改动多时直接写进快照（`cae95c3`、`91f43ba`）

高负载下发现：写侧存储的全量很慢（20 个百万成员集合要编码 2 秒以上），改动约 20% 时的差量也比视图模式慢。所以：

- 两次 checkpoint 之间改动超过 10%（`GARNET_SORTEDSET_DELTA_RATIO`）时，直接写进快照，和视图模式一样；改动降下来后再写一次
  侧存储全量重新建链。第一次 checkpoint 和恢复后的第一次也会走快照（所有成员都算新加入）。
- 改动列表的内存上限放宽到集合的一半，并且和这个门槛分开。之前上限是 1/4，长时间写全量期间的写入会把列表撑爆，
  导致基版本被丢弃、下一次又只能写全量，形成循环。

### 4.6 研究用记录

分段计时（找改动、编码、提交、fsync）、服务器内存、每 10 ms 的 GC 采样、运行时 GC 事件（每次 GC 的代数、类型、原因、线程暂停的起止）。

## 5. 正确性验证

- **单元测试**（`test/standalone/Garnet.test.collections/`）：
  - `SortedSetSideStoreTests`（7 个）：链的写读、删了又加、缺文件 / 损坏 / 截断 / 别的集合的文件都必须报错、未提交的文件被删、清理规则。
  - `SortedSetMemberStoreDeltaTests`（4 个）：随机地反复改分、删除、重新插入同一成员，写入分别发生在视图边界之前、之后和读取过程中；
    把"基版本 + 差量"和边界时的状态逐个成员比对。故意放入 bug（差量读取时忽略旧分数）时测试会失败。
- **端到端**（`delta/delta_correctness.py`，缓存模式和 `--storage-tier` 两种都跑过）：7 次 checkpoint 期间一直写入后 kill -9
  恢复（结果是一致的前缀）；删除集合后重建、恢复后继续写再恢复；checkpoint 进行中 kill -9 共 8 次（结果总是上一次或进行中那次）；
  checkpoint + AOF 重放。
- **回归**：所有集合强制走增量（阈值 1）时，SortedSet 测试 362 个、集合相关测试 853 个全过；Garnet 主测试集在开关开和关时结果相同
  （同样 4 个本机端口检测测试失败，与改动无关）。

## 6. 实验和主要结果（cygnus）

设置：服务器绑 NUMA 节点 1（48 个逻辑 CPU），压测客户端绑节点 0；20 个 SortedSet × 100 万成员；ZSCORE / ZINCRBY，均匀随机；
`-m 32g`，缓存模式。结果目录都在 `research/checkpoint_cou/` 下，每个都有 RESULTS.md。

1. **checkpoint 期间的吞吐量**（16 个连接）：原版只剩平时的 1–56%；视图 + 池化、增量都保持约 90–110%。平时吞吐量三种配置相同。
2. **checkpoint 时长随改动比例变化**（`delta/results_cygnus_churn/`，16 个连接，第 3–6 次）：

   | 两次 checkpoint 之间改动的成员 | 原版 | 视图 + 池化 | 增量 |
   | --- | --- | --- | --- |
   | 0.16% | 0.58–0.88 s | 约 0.95 s | 20–34 ms |
   | 0.8% | 0.49–0.76 s | 约 0.97 s | 63–114 ms |
   | 3.1% | 0.46–0.69 s | 约 0.99 s | 0.22–0.32 s |
   | 7.4% | 0.51–0.66 s | 约 1.0 s | 0.51–0.60 s |
   | 约 14%（超过门槛，走快照） | 0.45–0.69 s | 约 1.05 s | 约 1.03 s |

   增量的时长大致和改动的成员数成正比，和视图模式的交叉点约在改动 13–15%。
3. **代价**：增量第一次 checkpoint 走快照（约 1.8 s，与视图相同），第二次建链约 2.5 s；恢复 11.3–11.8 s，原版 7.4 s。
4. **无负载 SAVE**（参照论文 8.6 节）：改 1% 后第二次 SAVE，原版 0.93 s，视图 0.98 s，增量 0.37 s（只写 4 MB）。
5. **小集合对照**（参照论文图 7a，Resp.bench ZADDREM，1024 个几乎为空的集合）：成员模式在 1–16 个连接时慢 8–16%，64 个连接时无差别。
6. **高负载**（64 / 128 个连接）：在 10% 门槛之前，增量的 checkpoint 2–4 s，比视图模式（约 1.5 s）慢；门槛改动后的高负载扫描正在跑。

## 7. 发现与澄清

- **`-m 8g` 下集合"丢数据"不是 Garnet 的 bug**：我们的实验没有开 `--storage-tier`，主日志是空设备，Garnet 相当于缓存，超过内存上限
  就淘汰整个 key。开存储层后同样负载不丢。之后所有实验用 `-m 32g` 并在结束时检查集合大小。（这一点由外部审阅指出。）
- **剩下的长停顿全部来自 GC**：所有连接同时停住 ≥ 100 ms 的情况，在视图和增量配置下 100% 与 .NET 运行时实际暂停线程的时间重合
  （例如 700 ms 对 713 ms）；原版约 2/3 是 GC，其余是序列化阻塞。触发原因几乎都是普通的小对象分配；连年轻代回收都要停 150–630 ms，
  成员存储让它更严重（每个成员多了引用）。
- **Garnet 用 Server GC**，后台 GC 默认开启，但仍有 1.1–1.4 s 的暂停。

## 8. 已知限制和下一步

限制：

- 小集合在低并发时慢 8–16%（成员存储对所有集合生效）。
- 侧存储的全量写入慢（编码是瓶颈），导致建链那次 checkpoint 约 2.5 s，恢复也比原版慢约 4 s。
- 只支持 SortedSet；集群模式、多数据库不支持增量。
- 实验都在缓存模式、单台机器上。

下一步候选：

1. 看 64 / 128 个连接的改动比例扫描（正在跑），确认 10% 门槛在高负载下是否合适。
2. 加快侧存储的全量编码（攒成大块写），缩短建链那次 checkpoint 和恢复。
3. 减少 GC 停顿：让成员存储对 GC 友好（例如 key 放进大块连续内存，减少每个成员的引用），或评估 GC 设置。
4. 小集合不使用成员存储（阈值 + 转换），或在论文中写明限制。
5. 相关工作调研（Redis/Valkey fork 快照、Valkey 无 fork 保存、Dragonfly 快照、Zigzag / Ping-Pong / CALC），确认新意。

## 9. 按文件的代码改动

相对分支出发点 `66172eb`（`git diff 66172eb..ckpt-delta -- libs test`），共 20 个文件，约 2760 行新增。

### Tsavorite（存储层，`libs/storage/Tsavorite/cs/src/core/`）

- **`Allocator/HeapObjectBase.cs`**（大对象的基类，改动最多，约 400 行）
  - 新增三个虚方法，供 SortedSet 实现：`TryOpenSnapshotView()`（打开视图，默认不支持）、`WriteSnapshotView(BinaryWriter)`
    （从视图写出，返回"写出的内容是否只对快照有效"）、`CloseSnapshotView()`。
  - `CacheSerializedObjectData()`（copy-update 时保住 v 版本的地方）：checkpoint 期间先尝试打开视图并登记，状态设为 `VIEW_OPEN`
    后直接返回，不再在请求线程上整体序列化；对象正在被刷盘读视图时，改成标记"已被取代"而不等待。不在 checkpoint 时的路径不变。
  - `Serialize()`（刷盘时调用）：对象空闲时先尝试打开视图，从视图写出（`FlushFromView`）；碰到 `VIEW_OPEN` 的对象就接手读它的视图。
  - 新增 `FlushFromView()`：从视图读出字节再写出；对象在读的过程中被取代时，把字节留给同一次 checkpoint 的后续刷盘使用。
  - 新增 `EndSnapshotView()`：checkpoint 结束时关闭没被读的视图、释放留下的字节。
  - 增量用：线程标记 `InSnapshotFlush`（当前是否在写快照文件）和 `serializedDataSnapshotOnly`；只对快照有效的字节在写主日志时写空对象。
  - 早期原型：预序列化结果的发布与复用（`PublishPreSerialized`、`TryPreSerialize`、`ResetPreSerialized`）、刷盘先捕获再写盘
    （`SerializeViaCapture`）。这些由开关控制，默认不启用。
- **`Allocator/SerializationPhase.cs`**：新增状态 `PRESERIALIZED`、`VIEW_OPEN`、`VIEW_FLUSHING`、`VIEW_FLUSHING_SUPERSEDED`。
- **`Allocator/CheckpointSnapshotViews.cs`**（新文件）：当前 checkpoint 打开的视图登记表，`Begin()` / `TryRegister()` / `End()`，
  结束时统一关闭。
- **`Allocator/SerializedObjectData.cs`**（新文件）：缓存的序列化数据。默认是一个数组；`TSAVORITE_SERIALIZE_POOLED=1` 时是一串
  池化的 64 KB 块（带引用计数，最后一个使用者释放后归还）。`Capture(Action<BinaryWriter>)` 用来捕获视图的写出。
- **`Allocator/CheckpointPreSerializer.cs`**（新文件，早期原型）：checkpoint 开始时用后台线程把大对象提前序列化
  （`TSAVORITE_PRESERIALIZE_THREADS`），以及刷盘捕获的共享（`TSAVORITE_FLUSH_CAPTURE`）。默认关闭。
- **`Allocator/CheckpointObjectTrace.cs`**（新文件）：研究用 trace（`TSAVORITE_COU_TRACE`），记录 copy-update / 刷盘的序列化耗时
  和 checkpoint 阶段（`ckpt_prepare`、`ckpt_flushed`）。
- **`Allocator/ObjectAllocatorImpl.cs`**：在快照刷盘调用 `WriteRecordObjects` 前后设置 `InSnapshotFlush` 标记；实现
  `CollectPreSerializeCandidates()`（找出内存中的大对象，供预序列化原型使用）。
- **`Allocator/AllocatorScan.cs`**：`CollectPreSerializeCandidates()` 的默认空实现。
- **`Index/Checkpointing/SnapshotCheckpointSMTask.cs`**：在 checkpoint 的 PREPARE 阶段启用视图登记（`BeginSnapshotViews`），
  在 PERSISTENCE_CALLBACK 和 `OnAbort` 时关闭（`EndSnapshotViews`）；预序列化原型的启停；trace 打点。

### Garnet（服务器，`libs/server/`）

- **`Objects/SortedSet/SortedSetMemberStore.cs`**（新文件，约 660 行）：成员存储。
  - 默认模式是一个普通字典（和原来一样）；成员模式下是分段数组 + 按 id 串成的链 + 写锁。对外提供字典的接口
    （`TryGetValue`、索引器、`Add`、`Remove`、枚举）。
  - 快照视图：`TryOpenView()`（记边界）、`ReadInto()`（按链分批读，写者优先）、`Done()`、`Close()`（只处理保留了旧值的槽位）；
    `Capture()` 在成员第一次被改时记旧分数。
  - 增量用：每个成员的修改轮次（`ModEpoch`）、每轮改动 / 删除计数、被删的 key（`deletedKeys`）、改动列表（`changedSlots`）、
    `TryBeginChangeListRead()` / `ReadChanged()`（只读改过的成员）、链状态（`Uid`、`BaseSeq`、`ChainStart`、`ChainDeltas`）、
    `CommitChainFile()` / `ResetChain()` / `DropBase()`。
- **`Objects/SortedSet/SortedSetObject.cs`**
  - 内部字典 `sortedSetDict` 的类型从 `Dictionary<byte[], double>` 改成 `SortedSetMemberStore`；`Dictionary` 属性在成员模式下返回副本，
    保证所有写入都经过成员存储。
  - 实现 `TryOpenSnapshotView()` / `WriteSnapshotView()` / `CloseSnapshotView()`。`WriteSnapshotView` 决定写法：写主日志、小集合、
    改动超过门槛 → 内联写出（`WriteViewInline`，格式与原来相同）；否则写链文件 + 存根（`WriteViewToSideStore`：选全量或差量、
    从改动列表读、fsync、提交链状态、记录引用）。
  - 构造函数（从字节恢复）：读到存根标记 `-1` 时，从侧存储读链（`SortedSetSideStore.LoadChain`）并核对成员数。
- **`Objects/SortedSet/SortedSetSideStore.cs`**（新文件，约 420 行）：侧存储。链文件的写（`Writer`：写条目、`Commit` 时写尾部并 fsync，
  没提交就删除）、读（`LoadChain`：沿链回溯到完整版本再依次应用，逐个文件校验）、清理（`OnCheckpointCompleted` / `OnRecoveryCompleted`）、
  配置（`Configure`、`DisableWrites`）、开关（`DeltaMode`、`MinMembers`、`MaxDeltaRatio`）。
- **`Objects/SortedSet/SortedSetDeltaTrace.cs`**（新文件）：研究用 trace（`GARNET_DELTA_TRACE`），每个集合每次写出的方式、字节数、改动数和分段计时。
- **`Objects/SortedSet/GcTrace.cs`**（新文件）：研究用 GC 记录（`GARNET_GC_TRACE` 每 10 ms 采样；`GARNET_GC_EVENTS` 运行时 GC 事件）。
- **`StoreWrapper.cs`**：启动时配置侧存储目录（`<checkpoint 目录>/sortedset-members`，集群模式除外）并启动 GC 记录。
- **`Databases/DatabaseManagerBase.cs`**：checkpoint 成功后调用 `SortedSetSideStore.OnCheckpointCompleted()`；恢复后调用
  `OnRecoveryCompleted()`；非 0 号数据库做 checkpoint 时调用 `DisableWrites()`。

### 配置（`libs/host/`）

- **`Configuration/Options.cs`、`defaults.conf`**：新增选项 `--use-fold-over-checkpoints`（用 fold-over 代替 snapshot checkpoint，
  需要存储层），早期对比 fold-over 和 snapshot 的停顿时用。

### 测试（`test/standalone/Garnet.test.collections/`）

- **`SortedSetSideStoreTests.cs`**（新文件，7 个测试）：链文件的写读和各种损坏情况。
- **`SortedSetMemberStoreDeltaTests.cs`**（新文件，4 个测试）：改动列表读出的差量逐成员正确；未读的视图关闭后不泄漏。

## 10. 主要文件

- Tsavorite：`libs/storage/Tsavorite/cs/src/core/Allocator/HeapObjectBase.cs`、`SerializationPhase.cs`、`CheckpointSnapshotViews.cs`、
  `ObjectAllocatorImpl.cs`、`Index/Checkpointing/SnapshotCheckpointSMTask.cs`
- Garnet：`libs/server/Objects/SortedSet/SortedSetMemberStore.cs`、`SortedSetObject.cs`、`SortedSetSideStore.cs`、`SortedSetDeltaTrace.cs`、
  `GcTrace.cs`；`libs/server/StoreWrapper.cs`、`libs/server/Databases/DatabaseManagerBase.cs`
- 设计：`research/checkpoint_cou/INTEGRATION_DESIGN.md`（视图）、`research/checkpoint_cou/delta/DESIGN.md`（增量）
- 实验脚本：`research/checkpoint_cou/throughput/`、`research/checkpoint_cou/delta/paper_eval.py`、`delta_correctness.py`、`recovery_time.py`
