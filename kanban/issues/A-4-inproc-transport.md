# A-4 InProc Transport & Echo Sample

## Goal
实现进程内传输层与最小回环样例，验证 Actor 消息在同一节点内的 send/call 语义。

## Subtasks
- [x] 定义传输抽象接口（连接、发送、接收、事件回调）。
- [x] 提供 InProc 传输实现，支持本地短路与消息排队。
- [x] 编写 Echo 示例（Actor + 客户端）演示 send/call 往返流程。
- [x] 为传输与 Echo 示例补充单元测试/集成测试。
- [x] 更新文档说明如何运行样例。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- InProc 传输可在本地执行单元测试验证 send/call 正确性。
- Echo 示例在命令行可运行并打印成功响应。
- 框架保留远程透明语义（可配置关闭短路）。

## Test Cases
- [ ] `dotnet test`（含 InProc 传输与 Echo 示例测试）— 阻塞：执行环境缺少 .NET CLI，待线下验证。
- [ ] Echo 示例手动执行输出验证 — 阻塞：同上。

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-2 Core Actor Runtime & InProc Transport

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
- 2025-10-05：实现可配置的 InProc Transport（支持短路/排队模式），补充 Send/Call 集成测试并在 README 中描述选项；由于容器缺少 .NET SDK，自动化测试与示例运行待线下补验。
