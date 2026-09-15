# F-2 ConnectAsync 端口拒绝路径资源泄漏修复

## 状态
- 列：Done
- Owner: AI Agent（PM 执行）
- 复杂度：S
- 依赖：无（E-4 合并后暴露并记录：见 E-4 任务卡"顺手修复"节的遗留说明）

## 目标
修复 `TcpTransport.ConnectAsync` 中 `client.ConnectAsync` 本身失败（如端口拒绝、主机不可达）时的资源泄漏：
- `TcpClient` 未 Dispose（socket 泄漏直至 GC）；
- `connectCts` 未 Dispose。

## 子任务
- [ ] 阅读 ConnectAsync 现状，确认 connectCts 的生命周期归属。
- [ ] 失败路径 dispose client + connectCts；成功路径确认 CTS 在 InitializeAsync 后正确释放且不影响连接存续。
- [ ] 回归测试：连接被拒绝端口 → 抛出原始 SocketException（有界时间内），既有全量测试绿。

## 验收标准
- 泄漏路径结构性消除（代码评审确认 client/cts 在所有退出路径被释放）。
- 全量测试绿；新增回归测试确定性。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 连接拒绝端口 | 原始 SocketException 在有界时间内抛出 |
| 2 | 全量测试 | 301+ 全绿 |

## 相关文件
- `src/Skynet.Cluster/TcpTransport.cs`

## 开发记录
- 修复中发现泄漏比任务卡记录的更多一处：`connectCts` 在**所有路径**（含成功路径）都从未 Dispose，
  CancelAfter 的计时器会存活到超时触发为止。修复后 CTS 在 finally 中统一释放（dial 与 handshake
  结束后 token 不再被消费，释放安全）。
- ConnectAsync 重构为 try/catch/finally：catch 中 connection 非空走 `connection.DisposeAsync()`
  （幂等，含 _stream/_client），connection 未构造（如 GetStream 抛出）退回 `client.Dispose()`——
  与 E-4 在入站路径确立的同一套释放契约。
- 行为回归由既有 `CallAsync_ShouldFailFastAndDrainPendingCallsWhenConnectFails` 守护（refused dial
  有界失败 + pending 排空）；泄漏消除属结构性修复，以代码评审确认所有退出路径释放。
- 全量 301/301 通过。

## Review 意见
- 自审：三条退出路径（ConnectAsync 失败 / 构造失败 / InitializeAsync 失败）+ 成功路径均正确释放
  client（含 streams）与 connectCts；`return connection` 在 try 内、finally 释放 CTS 时连接已建立
  且不再消费 token，无竞态窗口。
- 未新增测试面（遵守 ADR-0002 API 纪律，不为测试加内部成员）；既有用例继续守护行为层。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | 连接拒绝端口 → 原始异常有界抛出 | ✅ 既有用例（修复后复跑） |
| 2 | 全量测试 | ✅ 301/301（含 TLS/chaos/持久化全部既有覆盖） |
| 3 | 退出路径释放审查 | ✅ 结构性确认（代码评审） |

无 P0/P1。**QA Passed**
