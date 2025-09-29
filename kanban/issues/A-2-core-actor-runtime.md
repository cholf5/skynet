# A-2 Core Actor Runtime & InProc Transport

## Goal
实现 Milestone 1 的核心功能：Actor 基类、ActorSystem/ActorHost、本地 handle/name registry 以及 InProc Transport，提供 Echo 示例与单元测试。

## Subtasks
- [ ] 设计并实现 `Actor` 抽象，基于 `System.Threading.Channels` 构建邮箱，确保顺序执行与异常隔离。
- [ ] 实现 `ActorSystem`/`ActorHost`，完成服务注册、handle/name 映射、生命周期管理与唯一服务支持。
- [ ] 提供 `Send`/`Call` API，支持 fire-and-forget 与请求-响应语义，并确保本地短路逻辑。
- [ ] 构建 InProc transport 抽象，打通消息 Envelope 流程。
- [ ] 编写 Echo 示例（位于 `src/Skynet.Examples/EchoServer`）展示基本调用链路。
- [ ] 为核心行为编写单元测试与基础性能测试（吞吐/延迟烟雾测试）。
- [ ] 更新文档（README/示例说明）并在 Kanban issue 中记录遇到的问题与解决方案。

## Developer
- Owner: AI Agent
- Complexity: L

## Acceptance Criteria
- Echo 示例可通过 `dotnet run` 在本地启动，并验证 `Send`/`Call` 行为正确。
- 核心单元测试覆盖 Actor 顺序性、异常处理、handle/name registry、InProc transport。
- 所有测试在 CI 中通过，且 README 提供运行步骤。

## Test Cases
- [ ] `dotnet test`（Core/Examples 测试用例）
- [ ] `dotnet run --project src/Skynet.Examples/EchoServer`
- [ ] 压测脚本（如有）能够完成基础吞吐测试并记录结果。

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`
- `kanban/issues/A-1-repo-skeleton.md`

## Dependencies
- A-1 Repository Skeleton & Tooling Bootstrap

## Notes & Updates
- 2025-09-29：任务创建，等待仓库骨架准备完成后启动。
