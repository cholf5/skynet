# Release & Deployment Guide

本指南汇总了 Skynet 的 NuGet 发布、CI 流程与容器部署要点，便于维护者在不同环境下重复利用同一套资产。

## 1. 打包策略

- 所有项目共享 `Directory.Build.props/targets`，统一版本号、作者信息、仓库链接、符号包与 Source Link 设置。
- 默认版本前缀为 `0.1.0`，可通过 MSBuild 属性 `VersionSuffix` 附加预发布标记（例如 `dotnet pack -p:VersionSuffix=rc1` → `0.1.0-rc1`）。
- `Skynet.Generators` 以 `analyzers/dotnet/cs` 形式打包，便于消费侧直接获取 Source Generator。
- `PackageReadmeFile` 指向 `docs/nuget/package-readme.md`，在所有包中展示统一的简介与链接。

## 2. 手动发布步骤

```bash
# 1. 准备输出目录
rm -rf artifacts/nuget

# 2. 打包（生成 nupkg + snupkg）
dotnet pack Skynet.sln --configuration Release --include-symbols --include-source

# 3. 可选：验证本地包
./scripts/verify-packages.sh

# 4. 发布到 NuGet（需要设置 NUGET_API_KEY）
NUGET_API_KEY=<your-key> ./scripts/publish-packages.sh
```

`scripts/verify-packages.sh` 会创建一个临时控制台应用，通过根目录的 `nuget.config` 优先解析 `./artifacts/nuget` 包源并执行一次 `dotnet run`，确保关键依赖可被引用与运行。

## 3. GitHub Actions 发布

工作流文件：[`.github/workflows/release.yml`](../.github/workflows/release.yml)

- 触发条件：推送 `v*` 标签或手动触发 `workflow_dispatch`。
- 步骤：还原 → 打包 → 上传工件 →（可选）调用 `dotnet nuget push`。
- 可选输入：`versionSuffix`，用于在手动触发时临时追加预发布标记。
- 需要机密：`NUGET_API_KEY`（如果希望在流水线中自动推送到 NuGet.org）。

## 4. Docker 部署示例

- 根目录的 [`Dockerfile`](../Dockerfile) 基于多阶段构建，先在 SDK 镜像中执行 `dotnet publish`，然后拷贝到精简的 `mcr.microsoft.com/dotnet/runtime` 镜像。
- 默认入口为 `dotnet Skynet.Examples.dll --gate` 并暴露 8080 端口，可通过 `docker run skynet/examples -- --rooms` 覆盖示例模式。
- 若需要集成外部配置，可利用 `-v` 挂载 JSON/YAML 或 `-e` 注入环境变量，在容器启动脚本中读取。

## 5. 常见问题

| 问题 | 解决方案 |
| --- | --- |
| `dotnet pack` 找不到 `package-readme.md` | 确认仓库中存在 `docs/nuget/package-readme.md` 并未被移动。 |
| 发布流水线无法访问 NuGet | 检查 `NUGET_API_KEY` 是否在仓库或组织层面配置为机密，并确认 Actions 允许访问。 |
| 验证脚本在 CI 中失败 | 请确保在运行脚本前安装 .NET 9 SDK，并在 Linux 环境下执行（脚本使用 Bash 语法）。 |

更多背景信息可参考 [`docs/PRD.md`](PRD.md) 以及各模块的专属文档（如 [`docs/redis-registry.md`](redis-registry.md)、[`docs/rooms.md`](rooms.md) 等）。
