# E-1 Gate 限流 hook（连接准入 + 入站帧限速）

## 状态
- 列：Done
- Owner: AI Agent
- 复杂度：S
- 依赖：无（D 系列已完成）

## 目标
落实 PRD 4.10 "Rate limiting hooks at Gate level (pluggable)"：为 GateServer 提供可插拔的限流 hook，支持
1. 连接准入限流（按远端 IP 限制并发连接数）；
2. 入站帧限速（按会话令牌桶限速，超速可 Drop 或 Close）。

设计原则遵循 PRD 第 2 节：框架只提供 hook + 默认实现，不启用则零开销。

## 子任务
- [ ] 定义 `IGateRateLimiter` 接口与 `GateRateLimitDecision`（Allow/Drop/Close）。
- [ ] 提供默认实现 `TokenBucketGateRateLimiter`：每 IP 并发连接上限 + 每会话令牌桶（无锁 CAS 实现）。
- [ ] `GateServerOptions.RateLimiter` 配置项；GateServer 在 `RunSessionAsync` 中接线（TCP 与 WebSocket 共用路径）。
- [ ] 单元测试：令牌桶突发/恢复、每 IP 连接上限、会话状态回收。
- [ ] 集成测试：Drop 语义（连接存活、帧被丢弃）、Close 语义（连接被关闭）、连接准入拒绝。
- [ ] 文档：`docs/gate-rate-limiting.md`。

## 验收标准
- 未配置 RateLimiter 时 GateServer 行为与现状完全一致（零开销路径）。
- 限流决策点覆盖 TCP 与 WebSocket 两条接入路径。
- 默认实现可配置：每 IP 最大并发连接数、入站帧速率（fps）、突发容量（burst）。
- 单元/集成测试全部通过，`dotnet test` 绿。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | burst=2 快速发送 2 帧 | 全部 Allow |
| 2 | 第 3 帧超出 burst | Drop（令牌不足） |
| 3 | 等待足够时间后再次评估 | Allow（令牌恢复） |
| 4 | maxConnectionsPerIp=1 时同 IP 第二条连接 | Close |
| 5 | 连接关闭后 OnSessionClosed 释放状态 | 同 IP 可再次接入、内部计数归零 |
| 6 | Gate 集成：limiter 对特定 payload 返回 Drop | 帧不被路由、连接存活、后续帧正常 |
| 7 | Gate 集成：limiter 返回 Close | 连接关闭，reason=ProtocolViolation |
| 8 | Gate 集成：连接准入 Close | session actor 未创建、socket 被关闭 |

## 相关文件
- `src/Skynet.Net/GateServer.cs`（RunSessionAsync 接线）
- `src/Skynet.Net/GateServerOptions.cs`
- `tests/Skynet.Net.Tests/GateServerTests.cs`（参考风格）

## 开发记录
- 新增 `IGateRateLimiter` + `GateRateLimitDecision`（Allow/Drop/Close），默认实现 `TokenBucketGateRateLimiter`
  （每 IP 连接上限 + 每会话令牌桶；CAS 无锁、惰性补充、无后台定时器）。
- `GateServerOptions.RateLimiter` 接线点在 `RunSessionAsync`，TCP 与 WebSocket 共用；未配置时零开销。
- 环境问题：本机仅有 .NET 10 runtime，仓库目标 net9.0。测试统一以 `DOTNET_ROLL_FORWARD=LatestMajor`
  运行（本地兜底，CI 仍为 9.0.x）。尝试安装 9.0 runtime 失败（权限）。
- 测试过程中的 bug：负向读取（等待被丢弃帧的回复超时）废弃的挂起读会偷走后续回复字节 →
  改用 router 消息计数断言 Drop 语义，消除 flake。
- 全量测试 209/209 通过（5 个测试项目）。

## Review 意见
- **审查发现 R1（已修复）**：`MarkActivity()` 原在限流判定之前执行，被 Drop 的垃圾帧可刷新空闲计时，
  攻击者可用被丢弃的流量永久保活会话绕过 idle timeout。已改为仅 Allow 的帧刷新活动。
- 审查确认：连接准入被拒时不创建 session actor、不触发认证回调；Drop 语义只影响入站帧；
  加密握手帧同样受限流约束（接口文档已注明 burst ≥ 3 的要求）。
- 连接级 `Drop` 视同 `Allow` 已在接口文档中显式说明。
- 结论：**通过**。

## QA 记录
按验收测试用例执行（对应测试全部自动化）：

| # | 用例 | 结果 |
|---|------|------|
| 1 | burst=2 快速发送 2 帧 | ✅ TokenBucketGateRateLimiterTests.InboundFrames_ShouldAllowBurstThenDrop |
| 2 | 第 3 帧超出 burst | ✅ 同上 |
| 3 | 等待后再次评估（令牌恢复） | ✅ InboundFrames_ShouldRefillOverTime |
| 4 | maxConnectionsPerIp=1 同 IP 第二条连接 | ✅ Connections_ShouldCapPerIpAddress |
| 5 | OnSessionClosed 释放状态 | ✅ OnSessionClosed_ShouldReleaseConnectionSlotAndBucket |
| 6 | Gate 集成 Drop：帧不路由、连接存活 | ✅ GateRateLimitingTests.DropDecision_ShouldSkipFrameAndKeepConnectionAlive（计数式决策器等价覆盖 payload 判定） |
| 7 | Gate 集成 Close | ✅ CloseDecision_ShouldTerminateSessionWithProtocolViolation |
| 8 | 连接准入 Close | ✅ ConnectionAdmissionClose_ShouldRejectBeforeSessionCreation |

额外覆盖：TokenBucketLimiter_ShouldThrottleThroughGate（真实令牌桶经 Gate 的端到端节流与恢复）。
无 P0/P1 问题。**QA Passed**
