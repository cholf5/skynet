# C-9 KCP/UDP 传输 + ReliableQueue 可靠层（远期）

## Goal
为实时性要求高的场景（可对话的 MMORPG 战斗同步）提供 KCP/UDP 传输选项，并在其上叠加可靠有序层。Sharpest 已 vendored kcp2k managed core（`Sharpest.Platform`）并有完整的 ReliableQueue 实现（滑动窗口/ack/快速重传/序号回绕，约 200 行零依赖），可评估直接移植。

## Subtasks
- [ ] 技术选型：managed kcp2k vs native ikcp adapter；确定握手协议与 Skynet 现有 TcpTransport 的抽象对齐方式。
- [ ] 移植 Sharpest `ReliableQueue`（含 `ReliableSegment`/`ReliablePacketType`），移植前修复两个已知问题：断链回调不摘除坏 segment 导致每 tick 风暴（`ReliableQueue.cs:114-118`）、RTO 无自适应（固定初始值×退避）。
- [ ] 补测试：序号回绕、ack 乱序到达、windowSize 边界压力场景。
- [ ] KCP 传输接入 `ITransport` 抽象与 Gate。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- KCP 传输可替换 TCP 传输跑通跨节点 send/call 集成测试。
- ReliableQueue 在模拟丢包/乱序/延迟下保序投递并最终完成或判定断链。
- 有压测数据（丢包率 vs 吞吐/延迟曲线）。

## Test Cases
- [ ] ReliableQueue 单元（对应并扩展 Sharpest `ReliableQueueTests`/`ChannelSSReliableTests`）。
- [ ] 模拟 5%/20% 丢包下的端到端 RPC 往返。
- [ ] KCP loopback/RPC（对应 Sharpest `tests/Sharpest.Tests/Kcp/`）。

## Related Files / Design Docs
- `E:\dev\cholf5\Sharpest\src\Sharpest.Platform\`（kcp2k vendoring，MIT）
- `E:\dev\cholf5\Sharpest\src\Sharpest.Core\Kcp\`
- `E:\dev\cholf5\Sharpest\src\Sharpest.Common\Network\ReliableQueue.cs`
- `src/Skynet.Core/ITransport.cs`、`src/Skynet.Cluster/TcpTransport.cs`

## Dependencies
- C-3 TcpTransport 健壮性三硬伤（抽象稳定后再做）
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。注意：TCP 之上不需要 ReliableQueue（纯开销）；此任务仅在引入 UDP/KCP 时执行。Sharpest Kcp 握手（SYNC1/2/3/F）是其自造的最小兼容形状，可重新设计。
