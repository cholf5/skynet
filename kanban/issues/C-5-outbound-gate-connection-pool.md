# C-5 移植 Gate 出站连接池（多 endpoint + 重连 + round-robin）

## Goal
为 Skynet 补上"服务 → 多 Gate 出站连接池"能力（落在 `src/Skynet.Net/Gate/`）：多 endpoint 连接池管理、断线指数退避重连、round-robin 选路。填补 Skynet 只有入站 Gate、没有出站连接管理的空白（对应 skynet 原版 cluster 节点间出站连接的语义）。

## Subtasks
- [x] 实现核心类型：`GateClientManager`（Add/Update/Remove、同名 endpoint 地址变更替换连接、幂等）、`GateClient`（ConnectWithRetriesAsync、断线重连）、`GateProxy`（Volatile.Read/Interlocked.Exchange 无锁热替换）、`GateEndpoint`；payload 保持不透明 `ReadOnlyMemory<byte>`。
- [x] 实现 `ReconnectPolicy`（maxAttempts/initialDelay/maxDelay/backoffFactor + 参数校验）与可注入 `IReconnectDelayStrategy`。
- [x] 保持协议无关：构造参数为 `IReadOnlyList<string> endpoints` + `ReconnectPolicy` + `IGateClientTransport`。
- [x] 提供真实 TCP 实现 `TcpGateClientTransport`（与入站 GateServer 相同的长度帧协议）。
- [x] 重连行为测试（fake transport + fake delay strategy）。
- [x] 示例与文档：`docs/gate-connection-pool.md`。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 多 endpoint 下：不可达 endpoint 不阻塞启动；远端断线自动重连并替换连接；重试耗尽停在 Disconnected 且可再启动。
- round-robin 只选 Connected 状态，空池返回 null 不死循环。
- 无内部参考框架的协议依赖。
- 测试全绿。

## Test Cases
- [x] 正常连接 / 重连替换 / 重试耗尽 / Stop 取消 / 重复 Start 防双循环。
- [x] round-robin 选路与空池行为。
- [x] endpoint 地址变更时连接替换。
- [x] 真实 GateServer TCP 端到端回环。

## Related Files / Design Docs
- `src/Skynet.Net/Gate/`
- `tests/Skynet.Net.Tests/GateConnectionPoolTests.cs`
- `docs/gate-connection-pool.md`

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。
- 2026-09-13：完成。commit `8df414e`，验收 79/79。并发细节（无锁热替换、锁外触发事件、Stop 后拒绝僵尸连接）保留。
