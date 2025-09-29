# A-6 GateServer & Session Pipeline

## Goal
实现 GateServer（TCP/WebSocket）与 SessionActor 管线，支持外部客户端连接与消息转发。

## Subtasks
- [x] 设计 GateServer 抽象与配置（监听端口、协议、限流）。
- [x] 实现 TCP WebSocket 双协议接入与 SessionActor 绑定。
- [x] 提供消息路由策略，将客户端消息转发给内部 Actor。
- [x] 支持断线重连、Session 生命周期管理、心跳超时处理。
- [x] 编写示例客户端演示登录/回声流程。
- [x] 编写集成测试覆盖连接、重连、异常关闭。
- [x] 更新文档说明部署、接入方式与安全注意事项。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- GateServer 可同时接受 TCP 与 WebSocket 客户端连接。
- SessionActor 能正确接收与发送消息给客户端。
- 断线后旧 Session 被释放，新 Session 建立并恢复通信。

## Test Cases
- [ ] `dotnet test`（GateServer & Session 集成测试） — 环境缺少 .NET CLI，代码已补充 `Skynet.Net.Tests` 覆盖。
- [ ] 手动客户端示例验证 — 新增 `--gate` 示例模式，待本地运行确认。

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-5 TCP Transport & Static Registry

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
- 2025-10-01：实现 GateServer 抽象、SessionActor 管线与 TCP/WebSocket 接入，新增 `Skynet.Net.Tests/GateServerTests` 覆盖往返、重连与超时场景，并在示例程序提供 `--gate` 运行模式；受限于当前环境无法执行 `dotnet test` 与真实客户端验证。
- 2025-10-02：整理网关代码结构与示例入口，统一 Tab 缩进并强化会话关闭监测；仍因容器缺少 .NET CLI 无法执行 `dotnet test`。
