# C-3 TcpTransport 健壮性三硬伤

## Goal
修复 TcpTransport 的三个生产级缺陷：帧大小无上限（OOM）、心跳只发不检（假死链路发现不了）、连接断开不失败挂起的 pending calls（CallAsync 永久悬挂）。

## Subtasks
- [x] `TcpTransportOptions` 增加 `MaxFrameBytes`（默认 16MB），`ReadFrameAsync` 解帧前硬拒绝超限帧：断开该连接并记 Warning 日志（`src/Skynet.Cluster/TcpTransport.cs`）。
- [x] 心跳加接收侧检测：维护 per-connection `lastReceived`，超过 `N × HeartbeatInterval`（`DeadNodeGracePeriod`，默认 3×）未收到任何帧即判定假死并主动断开。
- [x] `ReadLoopAsync` finally 中遍历 `_pendingCalls`，将该节点关联的全部 pending call 以 `RemoteConnectionClosedException` fail-fast，不留悬挂 TCS。
- [x] 修复入站连接覆盖 `_connections` 的竞态：出入站连接共用同一 per-node gate 仲裁，保留既有存活连接。
- [x] 补充断线重连后 pending call 失败、假死检测、超大帧拒绝的集成测试。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 构造 `length = int.MaxValue` 帧的客户端被立即拒绝，服务端内存无异常增长。
- 对端进程被 kill 后，本端挂起的 `CallAsync` 在检测窗口内收到异常而非永久悬挂。
- 心跳超时能主动断开半开连接并有日志。
- 全部测试绿。

## Test Cases
- [x] 超大帧拒绝单测。
- [x] kill 远端进程 → pending call 异常返回集成测试。
- [x] 假死链路（不 close 只停发）心跳检测测试。

## Related Files / Design Docs
- `src/Skynet.Cluster/TcpTransport.cs`
- `src/Skynet.Cluster/TcpTransportOptions.cs`
- `src/Skynet.Cluster/RemoteConnectionClosedException.cs`

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。这是上一轮评审认定的传输层三大硬伤，Gate 侧已有帧上限（1MB）可对齐参考。
- 2026-09-13：完成。commit `e2eb710`，验收 35/35。
