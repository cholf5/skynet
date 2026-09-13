# C-5 移植 Gate 出站连接池（多 endpoint + 重连 + round-robin）

## Goal
从 Sharpest 移植"服务→多 Gate 出站连接池"能力到 Skynet（建议落在 `Skynet.Net` 或新项目 `Skynet.Cluster.Client`）：多 endpoint 连接池管理、断线指数退避重连、round-robin 选路。填补 Skynet 只有入站 Gate、没有出站连接管理的空白（对应 skynet 原版 cluster 节点间出站连接的语义）。

## Subtasks
- [ ] 移植核心类型：`GateClientManager`（Add/Update/Remove、同名 endpoint 地址变更替换连接、幂等）、`GateClient`（ConnectWithRetriesAsync、断线重连）、`GateProxy`（Volatile.Read/Interlocked.Exchange 无锁热替换）、`GateEndpoint`；payload 保持不透明 `ReadOnlyMemory<byte>`（来源 `E:\dev\cholf5\Sharpest\src\Sharpest.MicroKit\Gate\`，约 800 行）。
- [ ] 移植 `ReconnectPolicy`（maxAttempts/initialDelay/maxDelay/backoffFactor + 参数校验）与可注入 `IDelayStrategy`。
- [ ] 剥离 MicroKit/MicroServiceOptions 耦合：构造参数改为 `IReadOnlyList<string> endpoints` + `ReconnectPolicy`。
- [ ] 适配 Skynet 传输抽象（对接 `ITransport`/TcpTransport 或独立轻量 TCP 出站连接）。
- [ ] 移植/改写 274 行重连行为测试（fake transport + fake delay strategy）。
- [ ] 提供示例：服务进程通过连接池连多 Gate 并 round-robin 发消息。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 多 endpoint 下：不可达 endpoint 不阻塞启动；远端断线自动重连并替换连接；重试耗尽停在 Disconnected 且可再启动。
- round-robin 只选 Connected 状态，空池返回 null 不死循环。
- 移植后代码不含 MicroKit 协议依赖。
- 测试全绿。

## Test Cases
- [ ] 正常连接 / 重连替换 / 重试耗尽 / Stop 取消 / 重复 Start 防双循环（对应 Sharpest `MicroKitGateReconnectTests` 场景）。
- [ ] round-robin 选路与空池行为。
- [ ] endpoint 地址变更时连接替换。

## Related Files / Design Docs
- `E:\dev\cholf5\Sharpest\src\Sharpest.MicroKit\Gate\GateClientManager.cs`（Add/Update/Remove L153-260、SelectConnectedProxy L55-79）
- `E:\dev\cholf5\Sharpest\src\Sharpest.MicroKit\Gate\GateClient.cs`（L137-174, L183-208）
- `E:\dev\cholf5\Sharpest\src\Sharpest.Common\Client\ReconnectPolicy.cs`
- `E:\dev\cholf5\Sharpest\tests\Sharpest.Tests\MicroKit\MicroKitGateReconnectTests.cs`
- `src/Skynet.Net/`、`src/Skynet.Cluster/TcpTransport.cs`

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。Sharpest 借鉴清单第一梯队的自治组件之一；并发细节（无锁热替换、锁外触发事件、Stop 后拒绝僵尸连接）值得原样保留。
