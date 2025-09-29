# A-3 SourceGenerator & MessagePack Integration

## Goal
实现接口驱动的 RPC SourceGenerator，与 MessagePack 序列化集成，提供客户端 Proxy 与服务端 Dispatcher 的自动生成能力。

## Subtasks
- [ ] 设计 SourceGenerator 架构，解析带有 `[SkynetActor]` 标记的 interface 并生成代理/桩代码。
- [ ] 集成 MessagePack 序列化，支持 envelope 序列化/反序列化与自定义属性配置。
- [ ] 在示例项目中增加 Login 场景验证生成代码的端到端可用性。
- [ ] 编写单元测试覆盖常见接口模式（同步/异步、消息对象、多返回类型）。
- [ ] 更新文档，指导用户如何声明 interface 与配置 SourceGenerator。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- 编译期间自动生成代理代码，无需运行时反射。
- SourceGenerator 生成的代码通过单元测试验证（包括序列化往返与错误处理）。
- 文档新增“声明接口并生成代理”的示例章节。

## Test Cases
- [ ] `dotnet test`（包含 SourceGenerator 相关测试）
- [ ] 运行示例 Login 场景，确认客户端调用返回正确结果。

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`
- `kanban/issues/A-2-core-actor-runtime.md`

## Dependencies
- A-2 Core Actor Runtime & InProc Transport

## Notes & Updates
- 2025-09-29：任务创建，等待核心运行时完成。
