# A-9 Redis Registry Plugin

## Goal
实现 Redis 驱动的注册中心插件，提供分布式节点发现与 uniqueservice 协调。

## Subtasks
- [ ] 设计 Registry 插件接口与可插拔机制。
- [ ] 实现 Redis 注册与心跳逻辑（SETNX/EXPIRE/订阅）。
- [ ] 支持节点发现、服务映射、故障摘除。
- [ ] 提供配置示例与部署说明。
- [ ] 编写集成测试（可使用本地 Redis 容器或模拟）。
- [ ] 更新文档说明插件使用场景与限制。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 多节点可通过 Redis 注册与发现，实现 uniqueservice 协调。
- 节点异常退出能在 TTL 内被剔除。
- 插件遵循配置禁用时零侵入原则。

## Test Cases
- [ ] `dotnet test`（含 Redis 插件集成测试）
- [ ] 文档示例运行验证

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-8 DebugConsole & Observability Hooks

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
