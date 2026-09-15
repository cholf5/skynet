# F-2 ConnectAsync 端口拒绝路径资源泄漏修复

## 状态
- 列：To Do
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
（待填）

## Review 意见
（待填）

## QA 记录
（待填）
