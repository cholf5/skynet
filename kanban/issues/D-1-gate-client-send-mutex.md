# D-1 Gate 客户端发送互斥与接收帧上限

- **来源**：D 系列 Code Review（验收 C-5 时发现），P1 + P2
- **开发者**：AI Agent
- **估算复杂度**：S
- **依赖任务**：无（C-5 已合并）

## 目标

修复出站 Gate 连接的两个传输层缺陷：并发发送导致的帧流交错损坏（P1），以及接收路径无帧大小上限（P2）。

## 背景（Review 证据）

- `src/Skynet.Net/Gate/TcpGateClientTransport.cs:88-108`：`TcpGateProxyConnection.SendAsync` 将 header 与 payload 分两次 `WriteAsync`，且 `IsConnected` 检查与实际写入之间无互斥。多线程/多 Actor 扇出（`SendThroughAnyAsync` 的主用例）下，线程 A 的 header 可插到线程 B 的 header 与 payload 之间，服务端按 4 字节长度前缀解析将得到垃圾帧。移植偏差：Phonest 原型靠下游 `ProtobufChannel` 串行化发送；本仓服务端对照物 `TcpSessionConnection.cs` 有 `SemaphoreSlim` 发送锁，客户端恰恰漏掉。
- `src/Skynet.Net/Gate/TcpGateClientTransport.cs:141-147`：接收循环 `new byte[length]` 仅检查 `length < 0`，恶意/故障服务端发 `length=int.MaxValue` 可触发 OOM。服务端 `GateServer` 有 `MaxMessageBytes`（默认 1MB），客户端应对称防护。

## 子任务

1. `TcpGateProxyConnection` 增加 `SemaphoreSlim(1,1)` 发送锁，包住 header+payload 的完整写入；连接关闭时锁的获取需安全失败（dispose 后抛出或返回失败，不得死锁）。
2. `TcpGateClientTransportOptions`（或复用连接池 options）增加 `MaxFrameBytes`（默认对齐服务端 1MB），接收循环在分配前校验，超限关闭连接并记录日志。
3. 补并发测试。

## 验收标准

- 并发 `SendAsync`（≥8 并发 × 100 帧）后服务端解析出的帧流无损坏、无交错。
- 超大长度头的帧被立即拒绝并断连，不发生内存分配。
- 全量测试绿；`GateConnectionPoolTests` 现有用例不回归。

## 测试用例

1. 并发发送：N 线程同时 `SendAsync` 不同长度帧 → 接收端按序解出全部帧且内容完整。
2. 恶意长度头：服务端模拟发送 `length = int.MaxValue` → 客户端断连且进程内存平稳。
3. 连接替换期间发送不抛未观察异常（与 `RemoteDisconnectReconnectsAndReplacesConnection` 组合）。

## 相关文件

- `src/Skynet.Net/Gate/TcpGateClientTransport.cs`
- `tests/Skynet.Net.Tests/GateConnectionPoolTests.cs`
- 参照：`src/Skynet.Net/TcpSessionConnection.cs`（服务端发送锁形态）

## Review 记录

- **规格审查**：✅ 通过（独立复跑 build 0 警告、全量测试、逐条核对无缺失无夹带）。
- **质量审查第一轮**：需修复后批准。C-1（Critical，审查者在本机 net9.0 实证复现）：`_sendLock.Dispose()` 在连接 Dispose 时使排队在 `WaitAsync` 的发送者永久挂死（`Release` 的 disposed 检查先于唤醒排队者，异常被 catch 吞掉）；另有 I-1（`_isConnected` 兼职"已断开"导致远程断开后 socket/锁从不释放）、I-2（测试固定 `Task.Delay(100)` flaky）、I-3（发送中途取消留半帧）。
- **修复（commit 4381205）**：删除 `_sendLock.Dispose()`（SemaphoreSlim 无非托管资源，随连接 GC）；独立 `_disposed` 标志；测试改事件驱动；取消语义定为**帧边界取消**（持锁后 header/payload/Flush 全用 `CancellationToken.None`，帧内不可能产生半帧，已提交帧不响应取消——取舍写入代码与接口注释）。新增确定性回归测试 `DisposeWakesSendersQueuedOnSendLock`（白盒持锁构造排队者，红-绿验证：加回 Dispose 即红）。注意：TCP 背压方案（服务端停读）在 Windows loopback autotuning 下无法确定性阻塞，故用白盒方案——复审者认可。
- **复审**：✅ 批准（四项独立验证通过，测试 10 连跑稳定；全量 140/140）。
- **合入**：分支 `task/d1-gate-client-send-mutex`（c413d2a + 4381205）已 merge 到 main。
- **遗留 Minor**（不阻塞，可并入 D-7）：M-2 options 校验风格与 `GateServerOptions` 不一致；M-3 `TcpGateProxyConnection` 构造器 5 参数膨胀；M-4 `await Task.CompletedTask` 占位。
