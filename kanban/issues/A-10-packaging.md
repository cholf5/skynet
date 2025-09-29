# A-10 Packaging & Deployment Assets

## Goal
完成 NuGet 包装、版本管理与部署资产，确保框架可发布与快速部署。

## Subtasks
- [ ] 配置项目打包元数据（PackageId、Authors、License、Repository）。
- [ ] 编写 `Directory.Build.props/targets` 以统一版本与打包设置。
- [ ] 提供示例 `nuget.config`、发布脚本、CI 发布流程。
- [ ] 准备 Dockerfile 与示例容器部署脚本。
- [ ] 更新 README 发布指南与版本策略。
- [ ] 编写验证脚本确保包可成功安装与运行示例。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 项目可执行 `dotnet pack` 生成可发布的 NuGet 包。
- CI 支持手动触发或标签触发发布流程。
- Docker 示例可启动运行并执行基本示例。

## Test Cases
- [ ] `dotnet pack`
- [ ] 发布脚本 dry-run
- [ ] Docker 示例运行验证

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-9 Redis Registry Plugin

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
