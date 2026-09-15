# A-10 Packaging & Deployment Assets

## Goal
完成 NuGet 包装、版本管理与部署资产，确保框架可发布与快速部署。

## Subtasks
- [x] 配置项目打包元数据（PackageId、Authors、License、Repository）。
- [x] 编写 `Directory.Build.props/targets` 以统一版本与打包设置。
- [x] 提供示例 `nuget.config`、发布脚本、CI 发布流程。
- [x] 准备 Dockerfile 与示例容器部署脚本。
- [x] 更新 README 发布指南与版本策略。
- [x] 编写验证脚本确保包可成功安装与运行示例。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 项目可执行 `dotnet pack` 生成可发布的 NuGet 包。
- CI 支持手动触发或标签触发发布流程。
- Docker 示例可启动运行并执行基本示例。

## Test Cases
- [ ] `dotnet pack` *(blocked: container 缺少 .NET SDK，无法实际执行)*
- [ ] 发布脚本 dry-run *(同上)*
- [ ] Docker 示例运行验证 *(同上)*

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-9 Redis Registry Plugin

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
- 2025-09-30：集中在 `Directory.Build.props/targets` 中补齐打包元数据，新增打包验证脚本与 GitHub Actions 发布流水线，更新 README 与 Dockerfile。所有测试脚本因当前环境缺少 .NET CLI 无法运行，已在测试用例中标注。
- 2026-09-15：**BUG 修复（P1）**：`release.yml` 在 job 级 `if:` 中使用了 `secrets` 上下文（`if: secrets.NUGET_API_KEY != ''`），GitHub 不允许该上下文出现在 job 级条件中，导致整个 workflow 文件被判定为无效。症状：远端 19 次运行全部在启动前失败（零步骤执行）；且因触发配置无法解析，每次 push 到任意分支都会创建一个失败运行，运行名显示为文件路径而非 "Release Packages"。修复：删除 job 级 `if`，将 NUGET_API_KEY 判断移入发布步骤脚本内（未设置 secret 时打印提示并 exit 0）。已用 actionlint 1.7.7 验证通过。修复后该 workflow 仅在推送 `v*` 标签或手动触发（workflow_dispatch）时运行，日常 push 不再触发。
