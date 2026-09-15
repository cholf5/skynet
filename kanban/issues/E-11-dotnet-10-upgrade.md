# E-11 升级到 .NET 10（net9.0 → net10.0）

## 状态
- 列：Done
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
- TFM：`Directory.Build.props` + 9 个 csproj 全部 net9.0 → net10.0；LangVersion=preview 不变（自动获得 C# 14）。
- 包：Microsoft.Extensions.Logging.Abstractions 8.0.1 → 10.0.1；System.Collections.Immutable 8.0.0 → 10.0.1；
  其余（MessagePack 3.1.8 / Roslyn 4.10 / Redis / xunit 等）保持不动（兼容，最小变更）。
- CI：dotnet.yml / release.yml 的 setup-dotnet 从 9.0.100-rc.1 → 10.0.101；Dockerfile DOTNET_VERSION 9.0 → 10.0。
- 文档：README、CONTRIBUTING、CLAUDE.md、AGENTS.md（C# 13/net9.0 → C# 14/net10.0）、docs/release-guide.md 更新；
  scripts/run-benchmarks.sh 移除 DOTNET_ROLL_FORWARD 兜底；docs/benchmarks.md 基线以 net10.0 重测更新。
- QA 期间发现并修复 3 个**遗留**打包 bug（升级前已存在，被其它错误掩盖）：
  1. NU5039：所有包从未把 package-readme.md 打入 nupkg（csproj 缺 None include）——在 Directory.Build.props
     集中为可打包工程注入。
  2. NU5128：Skynet.Generators（analyzer 型包，无 lib 输出）被 TreatWarningsAsErrors 升级为错误——在该工程
     NoWarn 抑制（Roslyn 组件包的标准处理）。
  3. scripts/verify-packages.sh 三处陈旧：重复注册已存在的 skynet-local 源、`dotnet add package --configfile`
     在 SDK 10 移除、示例代码使用早已不存在的 `ActorSystemOptions.TransportFactory` 属性——脚本重写为
     retarget 本地源 + 整体重写消费端 csproj + 走 packageSourceMapping restore。
- 全量测试 216/216（net10.0，**无任何 rollforward 环境变量**）；基准重跑与升级前差异 ±10% 以内。

## Review 意见
- 审查确认：仓库无 net9.0/9.0-rc 残留（唯一例外 docs/skynet-linus-code-review.md 第 73 行，属历史评审快照，
  保留原样不做篡改）；central package version 均在 nuget.org 存在（restore 实证）；CI SDK 与本机 10.0.101 对齐；
  Dockerfile 两个 stage 均走 ${DOTNET_VERSION} ARG，单点替换生效。
- 包版本选择采用最小变更策略：只升阻碍项（Extensions/Immutable 进 10.x），不连带升级无关依赖，降低回归面。
- verify-packages.sh 的修复超出"升级"字面范围，但它是 SDK 10 CLI 变更的直接受影响者且是本任务的 QA 门，
  属于应修范围；三处遗留 bug 修复均有 stash 实证（net9.0 下同样失败）证明非升级引入。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | Release 构建 sln | ✅ 0 error（TreatWarningsAsErrors 下无 warning） |
| 2 | 全量测试（无 rollforward） | ✅ 216/216（5 项目，net10.0） |
| 3 | 基准三场景可运行 | ✅ local-tell 1.64M ops/s；local-call p50 0.167ms；remote-call p50 4.612ms |
| 4 | grep 残留 net9.0 引用 | ✅ 仅历史评审快照命中（预期保留） |
| 5 | verify-packages.sh 端到端（pack → net10.0 消费端 → run） | ✅ "Package verification complete" |

无 P0/P1 问题。**QA Passed**
