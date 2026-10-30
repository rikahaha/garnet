# 本地核对记录

2026-10-30，基线 `350fa9910d541f7d9f7cbb364be4f10c1eff4dbb`。
只新增设计和本记录；没有修改运行时代码，没有成员原型的测试或性能结果。

| 检查 | 结果 |
| --- | --- |
| 初始 git status --short | 空 |
| 仓库与祖先 AGENTS.md 检索 | 未发现 |
| SDK | PATH 8.0.133；显式路径 `/usr/local/share/dotnet/dotnet` 为 10.0.403 |
| 现有恢复测试项目，单节点 no-restore build | 成功，0 警告，0 错误 |
| CheckpointSharedObjectMutationTests / ObjectLogScanTests | 未执行测试用例：vstest 本地通信端口 bind 被沙箱拒绝；不是测试通过 |
| git diff --check | 通过（新增文档另做文本检查） |
| 原版/共享结果/成员旧值三组测量 | 未运行 |

构建命令（已执行）：

```sh
/usr/local/share/dotnet/dotnet build libs/storage/Tsavorite/cs/test/test.recovery/Tsavorite.test.recovery.csproj --no-restore --verbosity quiet -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```

测试尝试（已执行，exit 1）：

```sh
/usr/local/share/dotnet/dotnet test libs/storage/Tsavorite/cs/test/test.recovery/Tsavorite.test.recovery.csproj --no-restore --filter 'FullyQualifiedName~CheckpointSharedObjectMutationTests|FullyQualifiedName~ObjectLogScanTests' --verbosity quiet -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```

失败位置：`Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.SocketServer.Start`，
`System.Net.Sockets.SocketException (13): Permission denied`。
这是沙箱阻止测试框架的本地进程通信，不是连接远程服务器，也不是测试用例失败。
“不要连接服务器”不应被扩大解释为禁止 vstest 本地通信。此次未提升权限重跑。
普通多节点尝试还遇到 MSBuild NamedPipeServerStream 权限错误，已取消；单节点构建解决了该构建问题，
但没有解决 vstest 通信限制。

未运行现有负载脚本：它会连接本机服务器、删除 work 目录并覆盖固定文件名。
未对历史 CSV 做任何更新或把历史测量误标为本次实验。

后续进展：已新增独立控制台辅助协议原型，见
[`member_protocol/README.md`](member_protocol/README.md)。上文记录的是此前的 Garnet 核对，
并不包含独立原型的验证结果。独立原型已经运行，不使用 vstest 或本地通信端口；
它不构成真实 Garnet/CPR 恢复测试。
