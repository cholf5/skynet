# C-9 KCP/UDP 传输 + ReliableQueue 可靠层

## Goal
为实时性要求高的场景（可对话的 MMORPG 战斗同步）提供 KCP/UDP 传输选项，并在其上叠加可靠有序层。采用 vendored kcp2k managed core（MIT）+ 移植成熟的 ReliableQueue 实现（滑动窗口/ack/快速重传/序号回绕，约 200 行零依赖）。

## Subtasks
- [x] 技术选型：managed kcp2k vendored 源码拷入（零新增 NuGet 依赖，隔离封装）；重新设计最小握手（可靠流上的单帧 node-id 交换）。
- [x] 移植 ReliableQueue（含 `ReliableSegment`/`ReliablePacketType`），移植前修复已知问题：断链回调不摘除坏 segment 导致每 tick 风暴、RTO 无自适应。
- [x] 移植中发现并修复第三个协议缺陷：窗口外数据不得 ACK（否则窗口停滞时静默丢消息）。
- [x] 补测试：序号回绕、ack 乱序到达、窗口边界、断链终态、RTO 自适应。
- [x] KCP 传输接入 `ITransport` 抽象（真实 UDP，完整 envelope/registry/pending-call 语义）。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- KCP 传输可替换 TCP 传输跑通跨节点 send/call 集成测试。
- ReliableQueue 在模拟丢包/乱序/延迟下保序投递并最终完成或判定断链。
- kcp2k 源码保留原 MIT 版权声明并在 NOTICE 声明。

## Test Cases
- [x] ReliableQueue 单元（含扩展的回绕/乱序/RTO/断链场景）。
- [x] 模拟 5%/20% 丢包下的端到端可靠投递（种子化丢包管道 + 虚拟时钟）。
- [x] KCP 真实 UDP 集成测试（跨节点 send/call/RPC proxy）。

## Related Files / Design Docs
- `src/Skynet.Cluster/Transport/Reliable/`、`src/Skynet.Cluster/Transport/Kcp/`
- `tests/Skynet.Core.Tests/Reliable/`、`tests/Skynet.Core.Tests/KcpTransportTests.cs`
- `docs/transport.md`

## Dependencies
- C-3 TcpTransport 健壮性三硬伤（抽象稳定后再做）
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。注意：TCP 之上不需要 ReliableQueue（纯开销）；此任务仅在引入 UDP/KCP 时执行。
- 2026-09-13：完成。commit `6fcf00c`，验收 136/136。交付边界：ReliableQueue 完整 + 真实 UDP KcpTransport；Gate 接入/加密握手联动/压测曲线留后续。
- 2026-09-15：**BUG 修复（P1，CI 偶发）**：`KcpStaleResponseTests.KcpTransport_ShouldDropStaleResponseWithoutLocalDelivery` 在 CI 2 核环境下偶发失败（run #27/#28，本地无法复现）。根因：测试用固定 150ms 定时取消，与 KCP 拨号握手竞争——慢机器上握手超过 150ms 时，`KcpTransport.ConnectAsync` 的取消路径会静默 dispose 连接并退休 conversation id（这是传输层唯一的静默关闭路径），请求根本没发出；node2 对握手段的重传（7 次 = KCP 重传预算）全部命中 "retired conversation" 丢弃日志，"late response" 从未产生，断言超时失败。修复：改为由远端 actor 在开始处理请求时发 `TaskCompletionSource` 信号，测试收到信号后再取消——精确对应"调用方在远端处理期间取消"的语义，消除对拨号速度的假设；stale-response 等待上限 5s→10s 增加 CI 余量。验证：4 个 CPU 满载进程挤占环境下连跑 15 次全部通过；全量 216 测试通过。
