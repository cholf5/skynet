# D-3 KcpTransport 加固对齐（默认死链检测 + 入站会话治理）

- **来源**：D 系列 Code Review（C-9 验收），2 × P1
- **开发者**：AI Agent
- **估算复杂度**：M
- **依赖任务**：建议在 D-8（KCP 拆分独立工程）之前完成，避免搬完再改

## 目标

KcpTransport 相对 C-3 加固后的 TcpTransport 存在两处实质性裸奔，补齐到同等防护水平。

## 缺陷清单（Review 证据）

1. **默认配置下死链检测完全失效（P1）**：
   - TCP 版把 `DeadNodeGracePeriod=Zero` 解析为 `3 × HeartbeatInterval` 兜底（`TcpTransport.cs:165-175`）；KCP 版 `HeartbeatLoopAsync` 仅在 `DeadNodeGracePeriod > TimeSpan.Zero` 时检测（`KcpConnection.cs:258`），而 `KcpTransportOptions.DeadNodeGracePeriod` 默认就是 Zero（`KcpTransport.cs:623`）→ 默认无任何死节点检测。
   - 叠加问题：vendored kcp2k 的 dead_link 信号被丢弃——`Kcp.cs:905-908` 置 `state = -1` 后，`KcpSession`/`KcpConnectionPump` 从不读取 `kcp.state`（kcp2k 自家的 KcpConnection 正是用它触发 OnDeadLink）。
   - 后果：对端静默死亡（半开连接）时 KCP 会话永久滞留，pending call 只能靠调用方自己的超时兜底。
2. **入站会话无界且无握手时限（P1）**：`KcpTransport.cs:252-270` `RouteDatagram` 对未知 conv id 直接 `CreateInboundSession`，每个会话起 pump + 两个任务 + PeriodicTimer，无数量上限、无认证、无握手完成时限（出站有 `ConnectTimeout`，入站没有）。叠加缺陷 1，伪造源可低成本制造永不回收的会话。

## 子任务

1. 补 `ResolveDeadNodeGracePeriod` 等价逻辑（Zero → 3 × HeartbeatInterval），与 TCP 语义对齐；在 pump tick 内检查 `kcp.State == -1` 触发 `_onFault` 清理会话并 fail-fast pending。
2. 入站会话：握手/首帧超时（可复用 `ConnectTimeout` 或新增 `InboundHandshakeTimeout`）；会话总数上限（超限拒绝并丢弃，记录日志/指标）。
3. 顺手修两个 P2：`KcpConnection.DisposeAsync` 的 `_disposed` 改 `Interlocked.Exchange` 守卫（当前 check-then-act，并发 Dispose 会双重 Cancel 并抛 ObjectDisposedException，`KcpConnection.cs:275-292`）；`HeartbeatLoopAsync` 补 `ChannelClosedException` 捕获（pump 故障后心跳任务未观察 fault，`KcpConnection.cs:270-272`）。

## 验收标准

- 默认配置下，对端静默 `DeadNodeGracePeriod` 后会话被回收、pending call 以 `RemoteConnectionClosedException` 快速失败。
- kcp 层报 dead_link（如窗口耗尽）时会话被主动清理。
- 超过上限的未知 conv 包被丢弃且不影响既有会话；握手超时的入站会话被回收。
- TcpTransport 现有等价测试（`CallAsync_ShouldFailPendingCallWhenSilentPeerExceedsGracePeriod` 等）在 KCP 侧有镜像用例。

## 测试用例

1. 静默对端：连接后停掉对端 → 默认配置下 pending 在 grace 期内失败，会话移除。
2. 会话洪泛：伪造 N > 上限个不同 conv 的数据包 → 会话数不超上限，既有会话不受影响。
3. 握手超时：建连后不发握手帧 → `InboundHandshakeTimeout` 后会话被回收。
4. 并发 Dispose：并发调用 `DisposeAsync` 无未观察异常。

## 相关文件

- `src/Skynet.Cluster/Transport/Kcp/KcpTransport.cs`、`KcpConnection.cs`、`KcpConnectionPump.cs`、`KcpSession.cs`
- `tests/Skynet.Core.Tests/KcpTransportTests.cs`
- 参照：`src/Skynet.Cluster/TcpTransport.cs:165-175`（grace 解析）、`TcpTransportTests.cs:154-195`（半开连接实测）

## 补充（D-2 复审发现的新增验收点）

- **会话复活漏洞**（C-9 既有行为，D-2 复审时发现）：KCP outbound 握手超时后会话从 `_sessionsByConv` 移除，对端**迟到的握手应答**随后到达会被 `RouteDatagram` → `CreateInboundSession` 以 inbound 身份复活该会话。实现死链/入站治理时一并堵住：inbound 会话的创建应要求对端先发握手请求（或对已知 conv 的复活设置明确规则）。
