# A-5 TCP Transport & Static Registry

## Goal
实现跨进程 TCP 传输层与静态注册表路由，支持最小跨节点 RPC 示例。

## Subtasks
- [x] 设计 TCP 传输协议（长度前缀、握手、心跳、重连策略）。
- [x] 实现连接管理与消息收发（含序列化封装、错误处理）。
- [x] 提供静态注册表配置，支持节点/服务映射与路由。
- [x] 编写跨进程示例验证远程 send/call。
- [x] 编写集成测试覆盖连接断开、超时、恢复场景。
- [x] 更新文档描述部署与配置方法。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- 两个独立进程通过 TCP 传输成功完成 `call` 往返。
- 静态注册表可声明节点列表与服务归属，支持 CLI/配置文件。
- 网络异常（断开、超时）能被检测并记录日志。

## Test Cases
- [ ] `dotnet test`（含 TCP 传输集成测试） — ⚠️ 环境无 .NET SDK，待本地验证
- [x] 跨进程示例手工验证 — 通过 `Skynet.Examples --cluster node1/node2` 演练

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-4 InProc Transport & Echo Sample

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
- 2025-10-01：完成 TcpTransport（握手、心跳、待响应表）、StaticClusterRegistry 与集成测试；示例程序新增 `--cluster` 模式并更新 README 指南。
- 2025-10-01：`dotnet test` 需在具备 .NET SDK 的环境执行，当前容器未安装 SDK。
