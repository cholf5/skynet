# Contributing to Skynet

感谢你愿意为 Skynet 做出贡献！为了保持项目的高质量与一致性，请遵循以下流程：

## 准备工作

1. 阅读 [docs/PRD.md](docs/PRD.md) 与 [AGENTS.md](AGENTS.md)，了解项目目标、架构与编码规范。
2. 在 [kanban/board.md](kanban/board.md) 上认领或创建任务，并同步任务状态。
3. 安装 .NET 9 SDK，并确保可以执行 `dotnet build` 与 `dotnet test`。

## 开发流程

1. 从 `main` 创建功能分支：`git checkout -b feature/<task-id>`。
2. 使用测试驱动开发（TDD）或至少补充必要的单元/集成测试。
3. 编写代码时遵循以下约定：
   - 缩进使用 Tab（宽度 4）。
   - Allman 风格大括号（`{` 需独占一行）。
   - 避免滥用 `var`，仅在类型显而易见时使用。
4. 更新关联文档（README、示例、任务备注等）。
5. 提交前运行：
   ```bash
   dotnet format
   dotnet build
   dotnet test
   ```
6. 编写清晰的提交信息与 PR 描述，说明修改动机、实现方式与影响范围。

## 代码审查

- 每个 PR 至少需要一名维护者审核。
- 审核通过后由维护者合并，合并前确保 CI 全绿。
- 对于破坏性变更，请在 PR 中清晰标注并同步更新文档。

## 行为规范

本项目遵循 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。任何违反行为将被警告或移除贡献权限。

谢谢你的贡献！
