# E-11 升级到 .NET 10（net9.0 → net10.0）

## 状态
- 列：In Progress
- Owner: AI Agent
- 复杂度：M
- 依赖：无（E-1..E-3 已合并）

## 目标
将全部工程从 net9.0 升级到 net10.0（C# 14），对齐本机已有的 .NET 10 SDK/运行时（10.0.101/10.0.1），
消除本地 `DOTNET_ROLL_FORWARD=LatestMajor` 兜底；同步更新 CI、Dockerfile、NuGet 包版本与文档。

## 子任务
- [ ] `Directory.Build.props` 与全部 csproj：TargetFramework → net10.0。
- [ ] `Directory.Packages.props`：Microsoft.Extensions.Logging.Abstractions 8.0.1 → 10.0.1，
      System.Collections.Immutable 8.0.0 → 10.0.1。
- [ ] CI（dotnet.yml / release.yml）：setup-dotnet 9.0.100-rc.1 → 10.0.101。
- [ ] Dockerfile：DOTNET_VERSION 9.0 → 10.0。
- [ ] 文档：README、docs/release-guide.md、docs/benchmarks.md（去掉 rollforward 兜底说明）、
      scripts/run-benchmarks.sh、AGENTS.md 语言版本约定（C# 13 → C# 14）。
- [ ] 全量构建 + 测试（不再需要 rollforward）。
- [ ] 重跑基准并更新 docs/benchmarks.md 基线表。

## 验收标准
- `dotnet build Skynet.sln -c Release` 0 警告 0 错误（TreatWarningsAsErrors 生效）。
- 全量测试在不设置任何 rollforward 环境变量的情况下通过。
- 仓库内无残留 net9.0 / 9.0-rc 引用（构建产物除外）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | Release 构建 sln | 0 error / 0 warning |
| 2 | 全量测试（无 rollforward） | 209+ 测试全绿 |
| 3 | 基准三场景可运行 | 输出合理数据 |
| 4 | grep 残留 net9.0 引用 | 仅历史记录/任务卡命中 |

## 相关文件
- `Directory.Build.props`、`Directory.Packages.props`、全部 `*.csproj`
- `.github/workflows/dotnet.yml`、`.github/workflows/release.yml`、`Dockerfile`
- `README.md`、`AGENTS.md`、`docs/*`、`scripts/run-benchmarks.sh`

## 开发记录
（待填）

## Review 意见
（待填）

## QA 记录
（待填）
