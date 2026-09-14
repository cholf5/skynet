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
