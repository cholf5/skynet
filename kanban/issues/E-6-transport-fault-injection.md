# E-6 传输层故障注入与 Chaos 测试

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 13.3 Chaos 测试：为 TCP/KCP 传输层提供可注入故障的包装（延迟、丢包、断连、乱序），
并建立节点 kill/restart 的集成测试套件，验证 CallAsync 超时语义与恢复能力。

## 子任务
- [ ] `FaultInjectingTransport`（装饰 `ITransport`/帧管道）：可配置丢包率、延迟分布、断开事件。
- [ ] 集成测试：节点重启重连、持续丢包下 ReliableQueue 语义、半开连接检测。
- [ ] （可选）进程级 chaos 编排脚本。

## 验收标准
- 故障注入下无消息损坏；节点恢复后调用可继续；所有断言超时有界（测试不挂死）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 20% 丢包 + 重传层 | 最终送达、无重复副作用 |
| 2 | 远端 kill → restart | CallAsync 期间超时报错，恢复后成功 |
| 3 | 延迟抖动 | 无乱序副作用（按序处理语义保持） |

## 相关文件
- `tests/Skynet.Transport.Kcp.Tests/Reliable/ReliableLossyPipeTests.cs`（既有故障注入参考）
- `tests/Skynet.Core.Tests/TcpTransportTests.cs`
