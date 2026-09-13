# D-8 KCP 传输拆分为独立工程 Skynet.Transport.Kcp

- **来源**：维护者提出——KCP 不应放在 Skynet.Cluster 工程内
- **开发者**：AI Agent
- **估算复杂度**：S
- **依赖任务**：建议在 D-3（KCP 加固）之后执行，避免搬完再改同一批文件

## 目标

把 KCP/UDP 传输（含 vendored kcp2k 第三方源码）从 `Skynet.Cluster` 拆到独立工程 `Skynet.Transport.Kcp`，作为传输插件发布。

## 背景

C-9 将 KCP 直接放进了 `Skynet.Cluster`。现状虽与 `TcpTransport` 的位置一致（TcpTransport 历史上也在 Cluster），但：

- KCP 携带约 1600 行 vendored kcp2k 第三方源码（`src/Skynet.Cluster/Transport/Kcp/ThirdParty/kcp2k/`），不应进入所有 Cluster 用户的依赖足迹——不用 KCP 的用户被迫分发这段代码；
- PRD §2/§4.9 的设计原则是"强制可插拔：transport 是插件点"，插件应有独立包边界（参照 RedisClusterRegistry 的插件定位）；
- `Skynet.Cluster` 的职责纯度应回归 registry/routing/uniqueservice 编排，传输实现全部外置（TCP 后续也可走同样的拆分路径）；
- 未来 NuGet 打包时 `Skynet.Transport.Kcp` 可独立发版，kcp2k 上游升级不牵动主包。

## 子任务

1. 新建 `src/Skynet.Transport.Kcp`（net9.0，引用 Skynet.Core），迁移 `Transport/Kcp/` 与 `Transport/Reliable/`（ReliableQueue 是为裸 UDP 预留的配套件，随 KCP 包走；KCP 自带可靠性不叠加它——见 `KcpTransport.cs:30-34` 文档）。
2. 共享类型下沉：`RemoteConnectionClosedException`、pending-call fail-fast 辅助、信封/握手帧类型等 TcpTransport 与 KcpTransport 共用的类型移到 Skynet.Core（internal → 可访问性按需调整），或抽 `Skynet.Core.Transports` 内部共享文件链接方式，二选一在 PR 说明取舍。
3. 第三方代码保持目录隔离（`ThirdParty/kcp2k/` + LICENSE/NOTICE 随包迁移），csproj 不向下游传递其源码。
4. 更新 `Skynet.sln`、`tests`（`KcpTransportTests`、`Reliable/` 移到 `tests/Skynet.Transport.Kcp.Tests`）、`docs/transport.md`、`Directory.Packages.props` 如有涉及。

## 验收标准

- `Skynet.Cluster` 不再包含任何 KCP/kcp2k 源文件与引用。
- 新工程测试全部迁过且绿；全解决方案构建/测试绿。
- README/docs 的模块结构与实际一致。

## 测试用例

- 迁移本身不改行为：以现有 `KcpTransportTests` + `ReliableQueueTests`/`ReliableLossyPipeTests` 全绿为准。

## 相关文件

- `src/Skynet.Cluster/Transport/**`（整体迁移）
- `src/Skynet.Cluster/TcpTransport.cs`（共享类型下沉的影响面）
- `Skynet.sln`、`docs/transport.md`
