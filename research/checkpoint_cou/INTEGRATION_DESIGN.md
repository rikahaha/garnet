# 成员级写时复制接入 Garnet 的设计（阶段 2）

2026-11-06，基于 `ckpt-cou` 分支。本文只写设计，没有修改 Garnet 代码。
"已核实"指在当前代码中确认过；"待核实"指实现前必须先确认的假设。
协议本身见 `member_protocol/`（原型、正确性测试与 cygnus 测量）。

## 0. 目标与第一版范围

目标：checkpoint 期间修改大 SortedSet 时，请求线程不再序列化整个对象，前台额外代价只取决于被修改的成员。

第一版范围：

- 只改 SortedSet（含 GEO，它复用 SortedSet 的字典）；其他对象类型不变；
- 成员数超过阈值的集合才使用新结构，小集合完全走原路径；
- 有成员 TTL 的集合不建立视图，走原路径；视图建立后若出现 TTL 操作，走"物化退回"（§7）；
- 用开关控制，默认关闭；关闭时行为与现在逐字节相同。

## 1. 核心论证：视图建立点 = 现在整对象序列化的点

现在的代码（已核实）：IN_PROGRESS 之后修改 v 版本的内存记录，`InternalRMW.CheckCPRConsistencyRMW` 强制新建 v+1 记录；
新记录 CAS 成功后、`PostCopyUpdater` 之前，`HeapObjectBase.CacheSerializedObjectData` 在持有该 key 记录锁（tag-chain XLock）的情况下，
把 v 对象整个序列化。此后 v+1 对象（浅复制，与 v 共享内部集合）上的修改都不再影响已保存的字节。

提案：在**同一个调用点、同一把锁下**，不做序列化，而是以 O(1) 建立快照视图（记录边界编号和成员数）。此后：

- 对共享集合的每一次修改都经过成员存储（§2），视图活动时先记录该成员的旧值；
- 刷盘时从视图读出边界时刻的内容。

因此，只要满足下面这一条不变式，提案保存的内容就与现在的整对象序列化完全相同：

> **不变式 I：视图活动期间，对共享成员集合的任何修改都经过成员存储的 Set/Remove，不存在绕过它的写入。**

§2 的修改点清单就是为了满足 I；漏掉任何一处都会产生错误的快照。

## 2. 成员存储（Garnet 服务器侧）

### 2.1 结构

`SortedSetObject` 现在有四个共享结构（已核实，`SortedSetObject.cs:144–160`、`Clone` 在 `:222–229`）：

| 字段 | 用途 | 是否写入快照 |
| --- | --- | --- |
| `sortedSetDict`：成员 → 分数 | 查找 | 是（`DoSerialize` 只遍历它） |
| `sortedSet`：(分数, 成员) 排序树 | 排名、范围查询 | 否 |
| `expirationTimes`：成员 → 过期时间 | 成员 TTL | 是 |
| `expirationQueue` | TTL 清理提示 | 否 |

改动：成员数超过阈值时，把 `sortedSetDict` 换成 **成员存储 `MemberStore`**（原型 `ArrayOwner` 的 Garnet 版：分段数组 + 成员 → 槽位字典 + 视图 + gate）。
排序树和 TTL 结构不变。`Clone()` 共享 `MemberStore` 引用，和现在共享字典一样。

`MemberStore` 对外提供：`TryGetScore`、`Set`（新增或改分数）、`Remove`、`Count`、只读枚举，以及视图操作 `OpenView`、`ReadInto`、`Done`、`Close`、`Materialize`（§7）。

### 2.2 必须改的写入点（已核实，共 14 处成员字典写入 + 5 处 TTL 写入）

| 文件:行 | 方法 | 改为 |
| --- | --- | --- |
| `SortedSetObject.cs:204` | 反序列化构造函数 | 直接建入 `MemberStore`（恢复时按阈值选结构） |
| `SortedSetObject.cs:319` | `Add`（ZUNIONSTORE 等构建新对象时用） | `store.Set` |
| `SortedSetObject.cs:695` | `DeleteExpiredItemsWorker` | `store.Remove` |
| `SortedSetObject.cs:720` | `SetExpiration`（过期即删） | `store.Remove` |
| `SortedSetObjectImpl.cs:148`、`:191` | `SortedSetAdd`（ZADD） | `store.Set` |
| `SortedSetObjectImpl.cs:230` | `SortedSetRemove`（ZREM） | `store.Remove` |
| `SortedSetObjectImpl.cs:360`、`:366` | `SortedSetIncrement`（ZINCRBY） | `store.Set` |
| `SortedSetObjectImpl.cs:638` | `SortedSetRemoveRangeByRank` | 逐个 `store.Remove` |
| `SortedSetObjectImpl.cs:811` | `PopMinOrMax` | `store.Remove` |
| `SortedSetObjectImpl.cs:866` | `SortedSetPopMinOrMaxCount` | `store.Remove` |
| `SortedSetObjectImpl.cs:1053` | `GetElementsInRangeByLex`（带删除） | 逐个 `store.Remove` |
| `SortedSetObjectImpl.cs:1133` | `GetElementsInRangeByScore`（带删除） | 逐个 `store.Remove` |
| `SortedSetGeoObjectImpl.cs:61`、`:71` | `GeoAdd` | `store.Set` |
| `SortedSetObject.cs:211`、`:737`、`:749`、`:777`，`InitializeExpirationStructures` | TTL 写入 | 视图活动时先物化退回（§7） |

范围删除和 POP 改为逐个 `Remove`，每个被删成员只记录一次旧值，代价与删除数成正比（本来就是）。
成员过期清理只在写路径上调用（已核实：`DeleteExpiredItems` 的 8 个调用点都在写命令中），符合 `DoSerialize` 注释"序列化不修改对象"的约定。

### 2.3 对外的 `Dictionary` 属性（已核实，`SortedSetObject.cs:237`）

外部只读：`SortedSetOps.cs` 的并集/交集用它（`:1221`、`:1256`、`:1538`、`:1543`）。
但 `:1538` 在单个 key 的 `ZINTER` 时**直接返回活字典的引用**给调用方。
换成 `MemberStore` 后，这个属性改为返回一份拷贝（这些命令本来就是 O(N) 读）。不对外暴露 `MemberStore` 的可写引用。

### 2.4 读路径与 gate

- 写路径：每次 `Set`/`Remove` 都拿 `MemberStore` 的 gate。前台写入之间本来就由记录锁互斥，gate 只用于和后台扫描线程互斥。
  原型测得平时 p99 多约 0.4–0.8 µs。
- 读路径（ZSCORE、ZRANGE 等）：扫描线程只改链表指针、已删除槽位和 `HasOld` 标志，不改字典，也不改存活成员的分数，
  所以读路径**可能**不需要 gate。**待核实**：对照 `ArrayOwner` 的实现逐项确认；不能确认就让读路径也拿 gate。
- 写入优先、启动预热（`WarmUpScanPath`）、批缓冲复用都沿用原型。

## 3. 序列化状态机（Tsavorite 侧，`HeapObjectBase`）

现有状态：`REST`、`SERIALIZING`、`PRESERIALIZED`（研究原型）、`SERIALIZED`。新增：

- `VIEW_OPEN`：该对象（v 版本的包装）有活动视图，v 数据可从视图读出；
- `VIEW_DRAINED`：刷盘已从视图写出 v 数据并关闭视图。

另加一个标志 `superseded`：该 v 包装已被一次 CopyUpdate 取代（新版本已存在）。

| 谁 | 看到的状态 | 动作 |
| --- | --- | --- |
| 首次 CopyUpdate（`CacheSerializedObjectData`，检查点中） | `REST` 且对象支持视图 | CAS `REST→SERIALIZING`，`OpenView()`，置 `VIEW_OPEN`，`superseded=true`，**不序列化** |
| 首次 CopyUpdate | `REST` 但不支持视图（小集合、有 TTL） | 现在的做法：整对象序列化 |
| 首次 CopyUpdate | `VIEW_OPEN` / `VIEW_DRAINED` | 只置 `superseded=true`（v 数据已有视图或已写出） |
| 首次 CopyUpdate | `SERIALIZING` | 等待（此时只可能是刷盘在做 O(1) 的建立视图，或小集合的整对象序列化） |
| 刷盘（`Serialize`） | `REST` 且支持视图 | CAS `REST→SERIALIZING`，`OpenView()`，置 `VIEW_OPEN`，然后分批读视图并写出 |
| 刷盘 | `VIEW_OPEN` | 分批读视图并写出 |
| 刷盘写完 | — | `Close()`，置 `VIEW_DRAINED` |
| 刷盘 | `SERIALIZED`（物化退回或小集合的字节） | 写出缓存字节（现有逻辑） |

要点：

- 无论 CopyUpdate 先到还是刷盘先到，都只做 O(1) 的建立视图，**请求不会再等刷盘序列化**（cygnus 上测到的 181–230 ms 等待）。
- 刷盘分批读：每批持 gate 最多 128 个成员，编码和写出在 gate 外，写线程可以穿插执行。
- 视图属于 v 包装；`MemberStore` 是共享的，一个时刻最多一个活动视图。
- 写出格式不变：v 包装自己的 `WriteType` + `base.DoSerialize`（对象级元数据），再写边界成员数和逐个成员。恢复代码不需要改。

**待核实**：刷盘对象日志的写出路径（含 PR #2081 的分块写出）在 `Serialize` 内分批写出是否安全，以及 `Serialize` 是否会在别的上下文（集群迁移 `MigrationChunkWriterAccumulator`、复制）被调用到 `VIEW_OPEN` 的 v 包装上。

## 4. checkpoint 生命周期

- **登记**：每次 `OpenView` 把对象登记到当前 checkpoint 的列表（与原型 `CheckpointPreSerializer` 的登记相同，同一把锁保证"复位开始后不再登记"）。
- **正常结束**：在 `SnapshotCheckpointSMTask` 进入 `PERSISTENCE_CALLBACK` 之前（与现在 `StopPreSerializer` 同一位置，此时会话仍在 WAIT_FLUSH，不会原地修改 v 记录）：
  - `VIEW_OPEN` 但未被刷盘读完的视图（例如记录在 `FlushedUntilAddress` 之下、不在本次快照刷盘范围内）：`Close()` 释放保留的节点和旧值，不产出数据；
  - `superseded=true` 的 v 包装 → `SERIALIZED`（无数据，与现在"被取代后写空"的语义一致）；
  - `superseded=false` 的对象（只有刷盘建立过视图，从没被修改）→ `REST`，它仍是存活对象。
- **中止**（`OnAbort`）与刷盘失败：同上关闭所有登记的视图；状态恢复规则相同。之后可再次 checkpoint。
- **下一次 checkpoint**：开始前所有视图必须已关闭（`MemberStore` 只允许一个活动视图）。由上面的关闭步骤保证。

## 5. 版本边界与事务

- 边界与现在相同：首次 CopyUpdate 时、持有记录锁的那一刻（§1）。
- v 版本事务（MULTI、Lua）在提交前持有它们的 key 锁；v+1 线程对同一 key 的 CopyUpdate 必须等锁，所以边界在事务提交之后。
  状态机在离开 IN_PROGRESS 前等待 v 事务排空（`StateMachineDriver.TrackLastVersion`）。**待核实**：这条论证与现有整对象序列化依赖的是同一前提，需要用事务测试确认。
- PREPARE 阶段的 v 线程遇到 v+1 记录会 `CPR_SHIFT_DETECTED` 重试（已核实），不会在 v+1 已存在后修改共享集合。

## 6. 大小阈值与结构转换

- 阈值 T（初值约 1 万成员，评估中确定）。
- 对象成员数在一次写入中越过 T 时，在**该写入内**把字典转换为 `MemberStore`：代价 O(T)，与对象最终有多大无关。
- 只在没有活动视图时转换。若转换发生在 checkpoint 期间：v 包装已用整对象序列化保存（小集合的路径），转换只替换 v+1 包装的字段，v 包装保留旧字典且不再被修改，所以安全。**待核实**：字段目前是 `readonly`，需要改成可替换并确认没有其他别名。
- 成员数降回 T 以下时不转回（避免来回转换）。
- 恢复时（反序列化构造函数）按成员数直接选结构。

## 7. 物化退回

当视图活动期间出现不支持的操作（第一版：任何成员 TTL 写入），执行"物化退回"：

1. 在当前写入的记录锁下，从视图读出全部边界内容，生成序列化字节（O(N)，这次请求会卡，计数）；
2. 把字节交给 v 包装（`SERIALIZED`），关闭视图；
3. 之后按现有逻辑执行该操作。

注意：视图已记录过旧值后，当前集合已混入新版本的修改，**不能**直接对共享集合做整对象序列化，必须经视图读出。
物化是协议的通用退回手段，以后其他不支持的情况也走这里。退回次数单独统计，不混入新路径的数据。

## 8. 内存核算与上限

- 平时：每成员比字典多约 42 B（原型，分段数组）；纳入对象的 `HeapMemorySize` 和日志大小统计（`logSizeTracker`）。
- checkpoint 期间：被删成员的保留节点；旧分数存在节点内，不额外分配。
- 第一版不设硬上限。原型里"超配额就拒绝"会在 CAS 之后抛异常，不能照搬到 Garnet。**待决定**：要不要设上限，以及超限时是物化退回还是在 CopyUpdate 前拒绝建立视图。

## 9. 不受影响与需要核实的路径

| 路径 | 判断 |
| --- | --- |
| AOF 与重放 | 不受影响（记录命令，不涉及对象内部结构） |
| 恢复（读 checkpoint） | 格式不变，只是反序列化时按阈值选结构 |
| Upsert / DEL 覆盖整个 key | 新建记录与新对象，不修改共享集合；**待核实** |
| 页面淘汰（eviction）时的序列化 | 只发生在只读区，对象不会再被原地修改；**待核实** |
| 合并（compaction） | 在 checkpoint 前执行（`TakeCheckpointAsync`）；**待核实**是否会与视图重叠 |
| 集群迁移、复制的对象序列化 | 可能在 `VIEW_OPEN` 的 v 包装或 v+1 包装上调用 `Serialize`；**待核实**，必要时让它们经 gate 读活数据 |
| 其他对象类型（Hash、List、Set） | 不变 |

## 10. 测试计划

1. 开关关闭：Tsavorite recovery 测试、Garnet 现有测试全部通过，行为不变；
2. 开关打开：
   - checkpoint 期间混合执行 ZADD、ZINCRBY、ZREM、范围删除、POP、GEOADD，`kill -9` 后恢复，逐集合比对（扩展 `correctness_check.py`）；
   - 刷盘先到与 CopyUpdate 先到两种顺序；
   - 物化退回：视图活动期间执行成员 TTL 命令；
   - 事务：checkpoint 跨越 MULTI/EXEC 与 Lua；
   - 连续 3 次以上 checkpoint、中途失败与中止后再 checkpoint；
   - 越过阈值的转换发生在 checkpoint 前后；
3. 用 trace 证明受支持路径上请求线程的整对象序列化次数为 0，物化退回单独计数。

## 11. 实现顺序

1. `MemberStore`（由 `ArrayOwner` 移植，加入只读枚举与 `Materialize`），带单元测试；
2. SortedSet 接入：阈值转换、14 处写入点、`Dictionary` 属性、`DoSerialize` 拆分（对象头 + 成员）；
3. `HeapObjectBase`：新状态、`superseded`、CopyUpdate 与刷盘的建立视图、刷盘分批写出；
4. `SnapshotCheckpointSMTask`：登记、结束与中止时的关闭和状态恢复；
5. 正确性测试（§10），再上服务器做性能评估。

## 11a. 实现状态（2026-11-10）

已实现第 1–4 步（开关 `GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`，默认关闭）。与上文设计的差异：

- **状态**：实际用三个状态代替 `VIEW_OPEN`/`VIEW_DRAINED` + `superseded` 标志，把"是否被取代"并进状态里，用一次 CAS 决定，
  避免刷盘结束时读标志与 CopyUpdate 置标志之间的竞争：
  - `VIEW_OPEN`：CopyUpdate 建立的视图，还没有刷盘在读（对象已被取代）；
  - `VIEW_FLUSHING`：刷盘对存活对象建立视图并正在读；CopyUpdate 此时 CAS 到下一个状态后**直接继续，不等待**；
  - `VIEW_FLUSHING_SUPERSEDED`：刷盘正在读已被取代对象的视图。
- **同一次 checkpoint 中一条记录可能被刷两次**（主日志和快照文件）。所以读视图的刷盘先把数据读成字节，
  对象已被取代时把字节保留为 `SERIALIZED`，供后续刷盘复用，checkpoint 结束时统一释放；对象未被取代时回到 `REST`。
- **登记**：`CheckpointSnapshotViews`，PREPARE 开始、进入 PERSISTENCE_CALLBACK 前（及 `OnAbort`）结束。
  多个库的 checkpoint 时间重叠时共用一个列表，由最后结束的那个统一关闭（只是多占内存，不影响正确性）。
  FoldOver checkpoint 不登记，因此不建立视图，走原路径。
- **成员 TTL 不需要物化退回**：有 TTL 结构的对象不建立视图；没有 TTL 结构的对象被取代后，
  克隆添加 TTL 时分配的是克隆自己的结构，不影响 v 包装。所以第一版没有实现 §7。
- 非 checkpoint 路径（`IsInNewVersion` 为假的 CopyUpdate）保持原行为，仍等待正在进行的序列化，因为它会释放源对象。
- 尚未实现：大小阈值与结构转换（§6）、内存计入（§8）、集群迁移路径的核实（§9）。

验证（本地 Mac）：

- `correctness_check.py` 新增场景 C：checkpoint 期间一个客户端持续写（ZADD、改分数、ZINCRBY、ZREM、ZPOPMIN），
  连续 3 次 checkpoint 后 `kill -9`，不用 AOF 恢复；检查每个集合恰好等于自身操作的某个前缀，且所有前缀来自同一个全局前缀。
  开关关闭和打开都 PASS；场景 A、B 两种模式都 PASS。
- 场景 C 的 trace（开关打开）：请求线程整对象序列化 0 次；建立视图 58 次，平均 2.5 µs，最长 97 µs；
  刷盘先到时 CopyUpdate 不等待 2 次；刷盘读视图 60 次，平均 12.3 ms，每个集合 3.7 MB。
- 现有测试：SortedSet 351 个（两种模式），Tsavorite recovery 246 个，Tsavorite 对象相关 8 个，全部通过。
  Garnet checkpoint 相关 100 个测试：两种模式各有 1 个与定时器相关的测试在整组运行时失败（不是同一个），
  单独重跑都通过（开关打开时 `ScheduledCheckpointRecoversStringAndObjectUpdates` 连续 10 次通过），判断为负载下的偶发失败。

## 12. 需要决定的问题

1. 阈值 T 的初值，以及是否允许通过配置修改；
2. 读路径是否拿 gate（§2.4），取决于核实结果；
3. 内存上限的策略（§8）；
4. 第一版是否就支持成员 TTL（把旧过期时间也记在节点里），还是先用物化退回；
5. 集群迁移与复制路径（§9）在第一版中是否需要覆盖，还是先在开关打开时不支持集群模式。
