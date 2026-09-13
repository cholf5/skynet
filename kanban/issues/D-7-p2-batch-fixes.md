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

## 验收标准

- 每项有对应测试或可观测断言（如 3 的 fail-fast 测试、4 的并发对账测试）。
- 全量测试绿。

## 测试用例

1. `MaxFrameBytes=0` 构造 options 抛参数异常。
2. start hook 抛异常的 actor：Call 在短时间内收到包含启动异常的失败。
3. 并发触发两次 `ConnectionRestored` → 对账日志仅一份 added/removed 计数。
4. void 代理向已 dispose 的 system 发送 → 观测钩子收到异常记录。

## 相关文件

见清单内引用。
