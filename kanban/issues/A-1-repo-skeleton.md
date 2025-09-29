# A-1 Repository Skeleton & Tooling Bootstrap

## Goal
搭建 Skynet 项目的基础代码结构与工程配置，确保仓库具备基础的构建、测试与贡献规范。

## Subtasks
- [x] 创建 `Skynet.sln` 解决方案与核心项目骨架（`src/Skynet.Core`, `src/Skynet.Cluster`, `src/Skynet.Net`, `src/Skynet.Extras`, `src/Skynet.Examples`).
- [x] 初始化测试项目（`tests/Skynet.Core.Tests` 等）并引入 xUnit 与 FluentAssertions。
- [x] 配置 `.editorconfig`、`.gitignore`、`Directory.Build.props` 等共享设置，统一编码规范。
- [x] 添加基础 CI（GitHub Actions）以执行 `dotnet build` 与 `dotnet test`。
- [x] 更新 `README.md`，描述项目目标、目录结构及本地运行/测试指引。
- [x] 添加 `CONTRIBUTING.md` 与 `CODE_OF_CONDUCT.md` 初稿。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 解决方案可在本地成功执行 `dotnet build` 与 `dotnet test`，CI 工作流同步通过。
- README/贡献文档描述完成度满足 PRD 对里程碑 1 的要求。
- 项目结构与编码规范符合 `AGENTS.md` 中定义的目录、命名与缩进约定。

## Test Cases
- [x] `dotnet build`
- [x] `dotnet test`

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-0 Initialize Kanban Workflow (已完成)

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
- 2025-09-29：任务转入 In Progress，开始搭建仓库骨架。
- 2025-09-29：完成解决方案与 CI 初始化，准备提交代码审查。
