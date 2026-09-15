# E-7 日志级别热 reload

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：S
- 依赖：无

## 目标
落实 PRD 5.4 "支持热 reload of certain config like logging level"：提供运行时调整日志级别的最小设施
（DebugConsole 命令 `loglevel <level>` + 文件监视可选方案），不改框架核心。

## 子任务
- [ ] DebugConsole 增加命令：查看/修改当前最小日志级别。
- [ ] （可选）`LoggingHotReload` 组件：watch appsettings/logging 配置文件变化并应用。
- [ ] 测试 + 文档（docs/debug-console.md 增补）。

## 验收标准
- 运行中修改级别后，新的日志输出立即生效，无需重启节点。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | `loglevel` 查询 | 输出当前级别 |
| 2 | `loglevel Debug` 后输出 Debug 日志 | 生效 |
| 3 | 非法级别名 | 报错且级别不变 |

## 相关文件
- `src/Skynet.Extras/DebugConsole*`
- `docs/debug-console.md`

## 开发记录
- 新增 `LoggingHotReloadProvider`（`ILoggerProvider`，仅依赖 `Microsoft.Extensions.Logging.Abstractions`，
  未给 Extras 引入 MEL 全家桶新依赖）：持有运行时可写的 `LogLevel? MinimumLevel`（默认 `Information`），
  `CreateLogger` 返回的包装 logger 在每次 `IsEnabled`/`Log` 时重新读取级别（`Volatile` 读写 int 字段，
  `int.MinValue` 作 "not configured" 哨兵），因此修改对新日志立即生效，无需重建 logger。
- 取舍 1（sink 方案）：provider 本身不落地输出，构造时可选包装一个真正输出的 sink `ILoggerProvider`
  （Serilog/NLog/自研均可）；不传 sink 时转发到 `NullLogger`（级别可改但无输出）。这样 Extras 不必内置
  console sink，依赖面不变；sink 生命周期归调用方，provider 的 `Dispose` 不释放它。
- 取舍 2（不改核心）：`ActorSystem`/各组件仍通过注入的 `ILoggerFactory` 拿 `ILogger`，本任务零改动。
  热 reload 生效前提是宿主把 `ActorSystem` 的 logger factory 建在挂了该 provider 的 `LoggerFactory` 上
  （docs 给出 `LoggerFactory.Create(b => { b.AddFilter(null, LogLevel.Trace); b.AddProvider(hotReload); })`
  示例，并说明 `NullLoggerFactory`/`NullLogger` 默认用法下 `loglevel` 无可观察效果的原因与范围）。
- DebugConsole 集成：`DebugConsoleCommandProcessor` / `DebugConsoleServer` 新增可选参数
  `LoggingHotReloadProvider? logLevelProvider`（默认 null，向后兼容）；命令 `loglevel [level]`：
  无参查询当前级别（未接 provider 或 pass-through 时显示 "not configured"），带参修改
  （大小写不敏感，合法值 Trace/Debug/Information/Warning/Error/Critical/None，返回 previous），
  非法值报错且级别不变。未接 provider 时给出如何接入的提示文本。
- 未实现（任务卡标注可选）：文件监视（watch appsettings）方案，被 provider 手动改级别方案替代。
- 测试：新增 `LoggingHotReloadProviderTests`（8 例：默认级别、运行时修改、按级别过滤、既有 logger 立即生效、
  pass-through 委托 sink、无 sink 丢弃、None 恒为 false、null category 拒绝）+
  `DebugConsoleCommandProcessorTests` 新增 7 例（help 提示、无 provider、查询、pass-through 查询、
  设置生效、大小写、非法值级别不变）。全部确定性、无 sleep。
- 全量验证：`dotnet build Skynet.sln -c Release` 0 error；`dotnet test Skynet.sln` 246/246 通过
  （基线 231 + 新增 15）。

## Review 意见
（待填）

## QA 记录
（待填）
