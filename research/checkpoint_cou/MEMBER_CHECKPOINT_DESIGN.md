# SortedSet 成员旧值检查点：代码核对与阻碍分析

日期：2026-10-30。状态：**设计审查阶段，未实现或启用运行时优化**。

后续新增了独立辅助协议验证，见 [`member_protocol/README.md`](member_protocol/README.md)。
本文保留接入 Garnet 前的调查结论；下面的“未实现”指 Garnet 运行时路径。
独立原型采用“删除后重插获得新编号”的方案，不要求沿用旧节点身份。

基线：`350fa9910d541f7d9f7cbb364be4f10c1eff4dbb`。开始时 `git status --short` 为空。
未 fetch、切换分支、升级 main、连接服务器或推送。未发现仓库或祖先目录中的 AGENTS.md。
本文不是已完成原型的性能报告；现有实验 CSV 不作为新方法的证据。

## 1. 已核对的真实路径

相对路径均以仓库根目录为基准。

| 位置 | 当前行为 | 对替代路径的约束 |
| --- | --- | --- |
| `libs/server/Objects/SortedSet/SortedSetObject.cs`，构造函数、Clone | 复制包装对象，共享 `sortedSet`、`sortedSetDict`、expiration 字典与队列 | 旧包装对象不能再直接遍历实时集合 |
| 同文件，DoSerialize | 输出基类元数据、Int32 成员数、每项长度/字节/Double；带 TTL 时增加标志和 Int64 | 格式允许无序成员，但计数必须准确，元数据必须来自旧包装对象 |
| 同文件，Dictionary 属性 | 无 TTL 时直接返回可变的内部字典 | 仅给 ZADD/ZREM 加钩子不能证明所有写入均受保护 |
| `SortedSetObjectImpl.cs` | ZADD、ZREM、ZINCRBY，以及 POP、范围删除等直接修改字典和树 | 每一处写入需要受同一个协议覆盖或在修改前拒绝 |
| `SortedSetObject.cs`，DeleteExpiredItems 等 | 成员过期也会直接删除字典项 | 无成员 TTL 必须是被强制执行的限制 |
| `Objects/SortedSetGeo/SortedSetGeoObjectImpl.cs` | GEOADD 直接修改同一个字典 | GEO 不能被当作无关类型漏掉 |
| `Storage/Session/ObjectStore/SortedSetOps.cs` | 聚合/差集等读取公开 Dictionary，有的路径复用它 | 需逐个审计别名的生命周期与后续变更 |
| `libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/Implementation/InternalRMW.cs`，CreateNewRecordRMW | CAS 新记录成功后，调用 CacheSerializedObjectData，再调用 PostCopyUpdater | 在 PCU 才决定跳过捕获已经太迟；在 CAS 后抛异常也不是已验证的拒绝方式 |
| `libs/server/Storage/Functions/ObjectStore/RMWMethods.cs`，PCU | 操作 Tsavorite 已建立的克隆，不再二次 Clone | 版本绑定和边界视图必须在此前安全发布 |
| `libs/storage/Tsavorite/cs/src/core/Allocator/HeapObjectBase.cs` | REST→SERIALIZING→SERIALIZED 捕获；PRESERIALIZED 可以被 CopyUpdate 接管 | 不能用 REST 表示“允许修改但旧包装仍可扫描” |
| 同文件，ClearSerializedObjectData | 释放 superseded 对象的缓存，但保留终止状态；只有仍存活的 PRESERIALIZED 回 REST | 删除旧值后不能让 superseded 包装重新读取共享实时字典 |

### 同步与版本

前台 CopyUpdate/PCU 位于存储写锁范围；代码明确标注持有 tag-chain XLock。
后台对象序列化依赖包装对象上的 SERIALIZING CAS 与前台捕获握手，不是共同持有
`lock(sortedSetDict)`。不同版本包装的序列化状态独立，但集合共享。
epoch 保证日志/映射的访问生命周期，不会把 Dictionary 枚举变成一致快照。

`InternalRMW` 在 IN_PROGRESS 中对旧版本内存记录强制创建新版本记录。
所以保护边界是实际的 v→v+1 记录/会话协议，不是 BGSAVE 请求到达的时刻。
旧 v 会话仍在排空时，不能提前把所有可见成员视为最终 v 状态。

`SnapshotCheckpointSMTask.StartPreSerializer` 在离开 IN_PROGRESS 后的
WAIT_INDEX_CHECKPOINT/WAIT_FLUSH 启动预序列化；它不能替代更早发生的首次 CopyUpdate 捕获。
成功路径在 PERSISTENCE_CALLBACK 前 StopAndReset；随后服务器
`DatabaseManagerBase.InitiateCheckpointAsync` 清理缓存字节。
`StateMachineDriver.RunStateMachine` 异常时先 FastForwardStateMachineToRest，
再调用各 task.OnAbort；Snapshot task 会 StopPreSerializer。
因此新协议需要独立的关闭/读者生命周期握手，不能假设 OnAbort 时前台仍被冻结。
`CompleteAsync(token)` 取消等待和实际状态机 RunAsync 的取消也必须区分；
不能用“等待者已取消”作为立即释放快照状态的依据。

### 独立基线

保留现有环境变量语义：`TSAVORITE_PRESERIALIZE_THREADS`、
`TSAVORITE_PRESERIALIZE_MIN_BYTES`、`TSAVORITE_FLUSH_CAPTURE`、
`TSAVORITE_SERIALIZE_POOLED`。共享结果和池化不是同一种优化。
基线复现须显式设置全部变量，避免继承启动环境污染。
新开关建议为 `GARNET_SORTEDSET_MEMBER_CHECKPOINT=1`，但**本次没有实现此开关**。

## 2. 不能直接实现的简化方案及反例

1. 只给更新写 old-score 字典：边界有 a，扫描前删除 a，当前字典不再包含 a。
   如果不另行发现被删成员，就漏项。用实时 Count 写头还会计数错误。
2. 分批普通 Dictionary 枚举：插入/扩容会使枚举器失效；重新从头扫描则可退化为
   批次数乘成员数。并发字典的弱一致枚举也不能证明边界集合。
3. 已扫描成员删除后重插：若重插创建了新可扫描身份，没有固定边界上限与身份规则，
   同一个成员可能被重复输出；按实时存在性排除又可能遗漏旧成员。
4. 第一次修改时才建立全量节点索引：需要 O(N) 初始化保护区，违背本实验边界。
5. 捕获几个成员后，不支持命令直接走 DoSerialize：此时实时字典已含新版本修改，
   序列化得到的是混合版本，原有整对象保护不能直接作为中途回退。
6. 失败时清空 old-score 表并回 REST：旧包装仍与新包装共享集合，后续 flush
   可能把新值序列化成旧值，或者在并发写入时枚举异常。

## 3. 候选稳定遍历协议（尚未证明/实现）

可行方向需要从对象建立时就维护稳定辅助结构，不能在 checkpoint 首次修改时补建。
建议共享 owner 管理：成员→稳定节点索引、分段槽表、单调节点编号、live count、
短时 gate、活动 generation 及扫描引用计数。原有字典和树仍服务命令查询。

- 创建/恢复对象时逐成员建立节点；每次正常新增、删除、更新同时维护索引。
  这是新增的正常运行 CPU、内存和恢复成本，必须计入实验。不能免费忽略。
- 在该旧版本首次被新版本取代时，在与修改相同的同步范围内绑定 checkpoint token、
  CPR 版本、边界 count、节点编号上限和旧对象元数据。O(1) 建立视图，不复制成员列表。
  若后台先开始，则以同一 gate 建立等价边界视图，并与 CopyUpdate 原子交接。
- 节点已有成员分数可作为未修改旧状态；未扫描节点首次修改时记录旧分数一次。
  边界后新增节点的出生编号超过上限，显式表示边界不存在，不输出。
  删除不立即回收边界内节点；删除后重插必须沿用该 generation 的成员身份，
  保留第一次旧分数，不能用新 score 覆盖。
- 后台在 gate 内按固定槽编号区间读一批，对每个边界节点选择旧值或未变实时值，
  将成员字节的有效引用/所有权、score 放进有上限的批缓冲，推进单调游标并标记已消费；
  编码与输出在 gate 外。成员数组必须私有不可变或在写入 owner 时拷贝，不能引用输入缓冲。
- 扫描先读的成员，其已消费批缓冲承担旧值生命周期；修改先捕获的成员，由扫描消费
  旧值。二者必须在同一个 gate 交接，不能先释放 gate 再标记 scanned。
- 扩容只扩充分段目录，不移动已发布节点；删除留下受 generation 保护的槽。
  每个边界槽只读一次，不能从字典开头重扫。checkpoint 结束后如何增量回收槽/重用编号
  仍需完成设计：无限追加 tombstone 不是可接受的最终方案。
- 输出旧基类元数据和边界 count，恰好写边界 count 个成员，保留当前恢复格式。
  如果发现计数不符，应使 checkpoint 失败，不能提交部分结果。

### 必须补齐的结构性缺口

这不是只替换 SortedSet.DoSerialize 的优化，至少跨三个层次：

1. **存储接口**：IHeapObject.CacheSerializedObjectData 当前没有 ObjectInput，也不能
   在 CAS 前安全决定命令支持性。需要明确的能力/版本视图握手，覆盖首次 CopyUpdate、
   后续原地更新、删除、过期、覆盖、重建、事务、后台 eviction 和日志扫描。
2. **共享 owner**：所有集合别名写入必须进入 owner 协议，公开 Dictionary 的可变别名
   需要收敛或验证；旧包装视图和实时包装必须分离。只添加一个 lock 不构成此证明。
3. **关闭协议与上限**：成功、失败、取消必须停止新视图、等待或安全持有已有读者，
   再释放 old-state；全局 REST 提前发布时也必须安全。达到内存预算后的行为必须
   在修改前确定，不能发生 CAS 后异常或把受保护旧状态丢弃。

建议第一版在任何成员优化发生前，对不支持对象选择原有整对象基线；一旦激活成员视图，
不支持修改只能在修改和 CAS 前明确拒绝，或者从边界视图安全完成快照后再执行。
后者可能前台 O(N)，必须标为回退并计数，不能混入单成员快路径数据。
尚未实现上述前置拒绝接口，因此本轮不发布可启用的实验代码。
这不意味着稳定遍历在理论上不可行，而是当前代码尚没有可直接复用的安全替代路径。

## 4. 内存与测试准入要求

正常内存：每个成员的节点、索引项、分段空位、成员字节副本；不止 old-score 字节。
checkpoint 内存：每 generation 首次旧值记录、删除后保留节点、批缓冲及实际输出缓冲。
建议全局/每对象分别预留预算，明确包含 allocator slack 与数组容量；达到上限前拒绝
后续受影响修改或安全结束/中止 checkpoint。池最大保留量不是活动分配的硬上限。
清理需同时验证计费归零和不早退还被后台引用的字节。

必须以真实 RMW/CPR 路径和恢复读取做确定性测试，不能仅测试 helper：

| 同步点/操作 | 参考模型应验证 |
| --- | --- |
| 批读取前/后更新 | 恢复旧分数，恰好一份 |
| 删除、插入、删除后重插 | 恢复边界身份，排除新成员 |
| 同成员反复更新 | 首次旧值保留一次 |
| 插入触发扩容 | 游标持续前进，无重复/遗漏 |
| 至少 3 轮 checkpoint | generation 不串用，旧包装不复活 |
| flush/编码失败、运行取消、等待取消 | 区分状态机生命周期，随后可再次 checkpoint |
| TTL、GEO、多成员、范围删除、key 删除/覆盖/重建、跨键事务 | 激活前基线或激活后前置拒绝，键状态与恢复均正确 |
| 预算用尽 | 无部分修改、无部分提交、读者引用仍有效 |

期望值按实际会话版本/记录版本生成；测试同步点要记录 token、版本、操作序号。

## 5. 本地验证与性能状态

没有新方法的正确性结果，也没有三组原型性能数字。未启动 Garnet 或连接任何服务器。
默认 PATH 的 dotnet 只有 SDK 8.0.133，但 `/usr/local/share/dotnet/dotnet` 有 10.0.403，
满足本仓库 global.json；未修改 global.json。
尝试对现有 Tsavorite 保护回归做 `--no-restore` 测试；普通多进程构建遇到
沙箱 NamedPipeServerStream 权限错误，单节点重试结果见同目录验证记录。

现有 `object_cou_latency.py` 会删除 `research/checkpoint_cou/work`，并连接本机 Garnet；
本轮未运行，也未覆盖任何历史 CSV。它当前用随机新增成员负载，不能直接满足确定性
低/高修改覆盖率、无 checkpoint 和完整内存计费的要求。

性能准入顺序：先用 trace 证明支持命令的前台 `cou_serialize` 次数/字节为零，
并单列回退次数；再比较对象大小 1K/100K/1M、修改覆盖率 1%/80%、无 checkpoint。
三组分别为本 SHA 的原版配置、仅共享结果、成员方案；固定相同数据、随机种子、
修改成员和到达计划。记录全程 p99/p99.9/max、吞吐、checkpoint 耗时、capture 时间、
gate 等待/持有时间、辅助结构/旧状态/输出缓冲字节和进程峰值 RSS。
不能将现有 cou trace 的 totalUs 或 waitUs 当成新 owner gate 的锁时间。

## 6. 后续服务器准备命令（只记录，未执行）

以下在服务器本地检出代码后执行；没有 SSH/远程执行命令。不执行旧脚本，避免删除历史输出。

```sh
git rev-parse HEAD
git status --short
dotnet --info
dotnet test libs/storage/Tsavorite/cs/test/test.recovery/Tsavorite.test.recovery.csproj --filter 'FullyQualifiedName~CheckpointSharedObjectMutationTests|FullyQualifiedName~ObjectLogScanTests'
dotnet test test/standalone/Garnet.test/Garnet.test.csproj --filter 'FullyQualifiedName~CheckpointFailureTests'
dotnet build main/GarnetServer/GarnetServer.csproj -c Release
```

成员方案尚未实现，当前不存在可信的第三组运行命令。待真实恢复测试、预算失败测试和
前台零整对象捕获证据通过后，再添加独立输出目录的三组测量驱动；不能用未实现的
环境变量启动现有二进制并把它标为成员方案。
