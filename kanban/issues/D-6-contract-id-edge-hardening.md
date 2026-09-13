# D-6 contract id 边界加固

- **来源**：D 系列 Code Review（C-2 验收），P2 × 2
- **开发者**：AI Agent
- **估算复杂度**：S
- **依赖任务**：无

## 目标

C-2 的 contract id 机制（FNV-1a 内容 hash + 生成器自动注册）架构正确，但有两个边界问题会在真实部署中咬人：

1. **泛型 payload 的 FullName 跨运行时漂移**：泛型类型的 `Type.FullName` 内嵌 assembly-qualified 泛型参数名（如 `List`1[[..., System.Private.CoreLib, Version=...]]`），跨运行时版本/TFM 的节点会 hash 出**不同** contract id（`PayloadContractRegistry.cs:148` 一带）。契约虽禁泛型接口，但未禁 `Task<List<int>>` 这类返回/参数类型。
2. **未知 contract id 丢帧不回 fault**：TCP read loop 捕获 `UnknownPayloadContractException` 后只丢帧不断连（`TcpTransport.cs:568-576`），不向对端回送 fault——对端发起方的 pending call 只能等断连才 fail-fast，版本漂移表现为静默挂起而非快速错误。

## 子任务

1. `RegisterCore`/注册路径显式拒绝 FullName 含 assembly-qualified 泛型参数的类型，给出可操作的错误信息（指引：payload 必须是具体具名类型，集合类型需显式包装类型）。
2. 未知 contract id 时向对端回送 `RemoteCallFault`（或专用 UnknownContract fault 帧），让发起方 pending 快速失败；注意避免 fault 循环（fault 帧本身未知时直接丢）。
3. 文档明确部署前提："全集群共享契约程序集"（`docs/transport.md` / `docs/architecture.md`）。

## 验收标准

- 注册 `Task<List<int>>` 类 payload 在注册时即报清晰编译期/启动期错误，而非运行时两个节点 id 不一致。
- 节点 A 发送节点 B 不认识的 contract id → A 侧调用方在 RTT 级别收到 fault 异常，连接保持。
- fault 帧本身无法解析时不产生回环。

## 测试用例

1. 泛型 payload 注册被拒，错误信息含类型名。
2. 未知 contract id 跨节点：A call B（B 无该契约）→ A 侧快速失败 + 连接存活（镜像既有 `TcpTransport_ShouldKeepConnectionAliveWhenUnknownContractIdReceived` 的对向场景）。

## 相关文件

- `src/Skynet.Core/Serialization/PayloadContractRegistry.cs`
- `src/Skynet.Cluster/TcpTransport.cs:568-576`
- `tests/Skynet.Core.Tests/PayloadContractRegistryTests.cs`（含金标 hash 断言，改动 hash 算法需同步金标）
