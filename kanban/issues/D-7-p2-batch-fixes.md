# D-7 P2 零散修复批

- **来源**：D 系列 Code Review 各模块 P2 清单归集
- **开发者**：AI Agent
- **估算复杂度**：S（单项都小，合并一张卡减少切换成本）
- **依赖任务**：D-1/D-2/D-3 合并后再做，避免同文件冲突

## 目标

归集本轮 review 发现的全部 P2 小项，一批修完。

## 清单

1. **`MaxFrameBytes=0` 静默关防护**（`TcpTransport.cs:702`）：`_maxFrameBytes > 0` 才生效。改为 options 校验拒绝非正值（不限制须显式 `int.MaxValue`）。
2. **void 代理入队异常静默吞掉**（生成器 `SkynetActorGenerator.cs:483`）：`_ = faulted.Exception` 无日志无指标。接入可观测钩子（至少 `ActorSystem` 级回调 + 指标计数）。
3. **start hook 失败的 actor 上 Call 永久挂起**（C-1 副作用）：`ActorSystem.cs:326-329` 去掉 `await host.Startup` 后，向启动失败的 actor 发送 Call 不再得到启动异常，而是永久挂起（`ActorHost.RunAsync` 启动失败后不排空 mailbox）。让 host 失败时 fail-fast 所有已入队消息。
4. **Redis 对账防重入**（`RedisClusterRegistry.cs:378-383`）：SE.Redis 对 interactive/subscription 两条连接都会触发 `ConnectionRestored`，可能并发跑两个 `ReconcileSubscription` 重复回放。加 `Interlocked`/信号量防重入。
5. **Redis 注册锁内做网络 I/O**（`RedisClusterRegistry.cs:152-195`）：`RegisterLocalActor` 持 `_localServicesLock` 期间多次 Redis 往返，Redis 慢时阻塞心跳线程的自愈刷新。把 I/O 移出锁，锁内只维护内存状态。
6. **KcpTransport 杂项**：`RouteDatagram` 每包 `payload.ToArray()` 多一次拷贝（`KcpTransport.cs:237,248,252`）——低优先级，量小可顺手。
7. **`GateEncryptionClientSession` 解码 `ExpireUnixMs` 后未使用**（`GateEncryptionClientSession.cs:71`）：让客户端提前感知过期（本地时钟比对 + 提前失败），或删掉解码。
8. **（D-1 审查遗留）Gate 出站连接 Minor 三项**：`TcpGateClientTransportOptions` 校验风格与 `GateServerOptions.Validate()` 统一；`TcpGateProxyConnection` 构造器 5 参数膨胀（改传 options）；`DisposeAsync` 的 `await Task.CompletedTask` 占位。
9. **（D-2 审查遗留）KCP 无主响应测试的空转断言**：`TcpTransportTests.cs` 中 `NotContain "Failed to deliver message"` 在回归时走 `TrySetException` 分支、该日志不会出现，断言无效——删除或改写（如断言 node2 未收到反向 fault 响应）。
10. **（D-2 审查遗留）`TcpConnection` 嵌套类声明缩进**：`TcpTransport.cs:581-582` 多一层 Tab，与同类嵌套 `PendingCall` 不一致。
11. **（D-3 审查遗留）KCP 握手看门狗残余 check-then-act**：`KcpConnection.cs:178-186`，`IsCompleted` 检查与 Dispose 之间可误杀刚完成握手的入站会话——改 `Task.WhenAny(_handshakeCompleted.Task, Task.Delay(timeout, token))`。
12. **（D-3 审查遗留）`_retiredConversations` 注释与事实不符**：`KcpTransport.cs:298-299` 声称"cannot grow without bound"但过期条目仅在该 conv 再有包到达时移除——改注释为准确描述，或加机会式清扫（条目超阈值时遍历剔除过期项）。
13. **（D-3 审查遗留）`KcpTransport.DisposeAsync` 的 `_disposed` 仍是裸 bool check-then-act**（`KcpTransport.cs:639-644`）——对齐 `KcpConnection` 已有的 `Interlocked.Exchange` 模式；顺带删 `KcpTransportTests.cs:604-607` 恒等包装 `GetConversationId`。
14. **（D-3 审查遗留）死链测试的固定等待**：`KcpTransportTests.cs:447` 的 `Task.Delay(200)` 改 `WaitForConditionAsync(() => transport2!._pendingCalls.Count == 1, ...)` 消除理论竞态。
15. **（D-6 审查遗留）响应帧 unknown contract 本地快速失败**：对端注册了本端不认识的响应 payload 类型时，当前丢帧、本端 pending 走超时；本端握有 MessageId + IsResponse，可本地直接 fail pending（零回环风险），RTT 级别收敛为即时失败。TCP/KCP 对称实现。
16. **（D-6 审查遗留）测试小项**：KCP 负断言去掉 2s 固定 sleep、匹配串从裸 `"888"` 收窄为 `"Rejected KCP envelope" + contract id` 组合（node1 挂 RecordingLoggerFactory）；`MessageEnvelopeSerializerTests.CreateDto` 硬编码 `version = 3` 改引用 `MessageEnvelopeSerializer.WireVersion`。
17. **（D-6 审查遗留）fault 发送 token 统一**：`TcpTransport.cs:511` / `KcpTransport.cs:634` 的 fault 发送用 `CancellationToken.None`，与 `SendResponseAsync` 的 `_cts.Token` 不一致（有 catch 兜底，安全但不一致）。

## 验收标准

- 每项有对应测试或可观测断言（如 3 的 fail-fast 测试、4 的并发对账测试）。
- 全量测试绿。

## 测试用例

1. `MaxFrameBytes=0` 构造 options 抛参数异常。
2. start hook 抛异常的 actor：Call 在短时间内收到包含启动异常的失败。
3. 并发触发两次 `ConnectionRestored` → 对账日志仅一份 added/removed 计数。
4. void 代理向已 dispose 的 system 发送 → 观测钩子收到异常记录。

## Review 记录

- **规格审查**：✅ 通过。17 项逐项核验（16 修复 + 第 6 项跳过成立：改 ReadOnlyMemory 需动 vendored kcp2k 与 PumpOperation，风险大于收益）；行为改动重点验证（生成模板单一改动覆盖所有 proxy、TryComplete+排空顺序论证、Redis 0/1/2 状态机推演、TCP/KCP 响应帧快速失败对称）；diff 卫生（TcpTransport +40/-12 无夹带、BOM/CRLF 干净）；独立复跑 184 绿。两个非阻塞缺口（SendEnqueueFailed 并发文档、TokenExpireUnixMs 无测试）已由实现者补齐（2ad2907）。
- **质量审查第一轮**：需修复后批准。Important 1：`OnSendEnqueueFailed` 的 handler 抛异常会逃逸为未观察任务异常（本项修复初衷即消除它）且跳过后续 handler。Minor 2/3/4 顺手项。
- **修复（a9ba0d1）**：Important 1 的修法有一处必要修正——审查建议的单块 try/catch 包 multicast `Invoke` **无法阻止跳过后续 handler**（同一 multicast Invoke 内执行），实现者改用 `GetInvocationList()` 逐 handler 隔离（回归测试先以整块实现，5s 超时抓住问题后改为逐个调用）。Minor 2（OCE 走 TrySetCanceled）、3（排空消息补 OnMessageProcessed 指标，OCE 记 true/异常记 false 对齐正常路径双语义）、4（删死字段）全部落实。
- **复审**：协调者抽查 diff 通过（质量审查者授权"修复后抽查三处即可"）。
- **合入**：分支 `task/d7-p2-batch-fixes`（8b266c1 + be74788 + 439b04a + 2ad2907 + a9ba0d1）已 merge 到 main。
- **遗留（归后续小卡）**：ActorRef 整文件历史坏缩进顺修；WhenAny 注释措辞收敛；OnlyOnCanceled 文档；transport 校验风格最终统一（ctor 内联 vs options.Validate()）。

## 相关文件

见清单内引用。
