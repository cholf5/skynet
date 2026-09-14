# Gate 限流（Rate Limiting）

GateServer 提供可插拔的限流 hook（PRD 4.10），默认 **不启用**（`GateServerOptions.RateLimiter == null` 时零开销）。

## Hook 接口

```csharp
public enum GateRateLimitDecision { Allow, Drop, Close }

public interface IGateRateLimiter
{
    // 连接准入：每条新连接在握手/认证之前评估一次。Drop 在此视同 Allow。
    GateRateLimitDecision EvaluateConnection(SessionMetadata metadata);

    // 入站帧评估：每帧调用（含加密握手帧，以便约束 RSA 握手开销）。
    // Drop = 丢弃该帧且连接保持；Close = 以 ProtocolViolation 关闭会话。
    GateRateLimitDecision EvaluateInboundFrame(SessionMetadata metadata, int payloadSize);

    // 会话结束（含准入被拒的连接）时回收每会话状态。
    void OnSessionClosed(string sessionId);
}
```

实现必须线程安全：每个会话的接收循环都会并发调用这些成员。注意入站帧限流的 burst 必须 ≥ 握手帧数
（内置协议最多 3 帧），否则加密握手会被限流拦截。

## 内置默认实现

`TokenBucketGateRateLimiter`（`Skynet.Net`）：

- **连接准入**：按远端 IP 限制并发连接数（超出返回 Close，连接直接被关闭）。
- **入站帧限速**：每会话一个令牌桶，令牌按 `inboundFramesPerSecond` 连续恢复；桶空时返回 Drop（丢帧保连）。
- 无后台定时器：令牌按流逝时间惰性补充，CAS 无锁实现。
- 暴露 `TrackedSessionCount` / `TrackedConnectionCount` 供运维观测。

## 用法

```csharp
var options = new GateServerOptions
{
    RouterFactory = ctx => new GameRouter(),
    RateLimiter = new TokenBucketGateRateLimiter(
        maxConnectionsPerIp: 16,
        inboundFramesPerSecond: 200,
        inboundFrameBurst: 32),
};
await using var gate = new GateServer(system, options);
```

自定义策略（例如超速直接断线、或按认证身份而非 IP 维度限流）实现 `IGateRateLimiter` 即可：

```csharp
options.RateLimiter = new CloseOnAbuseLimiter(); // 你的实现
```

## 行为细节

- 限流点位于 `GateServer.RunSessionAsync`，TCP 与 WebSocket 两条接入路径共用同一逻辑。
- 连接准入被拒时：不创建 session actor、不执行认证回调，连接直接关闭。
- Drop 语义只对入站帧生效：帧被静默丢弃（Debug 日志），出站方向不受影响。
- Close 语义：会话以 `SessionCloseReason.ProtocolViolation` 关闭，description 为
  `Inbound frame rate limit exceeded.`（入站帧）或 `Connection rejected by the rate limiter.`（连接准入）。

## 测试覆盖

- 单元测试：`tests/Skynet.Net.Tests/TokenBucketGateRateLimiterTests.cs`
- 集成测试：`tests/Skynet.Net.Tests/GateRateLimitingTests.cs`
