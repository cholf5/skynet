# E-2 Prometheus 指标导出插件

## 状态
- 列：To Do
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 4.9（Monitoring plugin point: prometheus hooks example）与 Milestone 7 的 observability 部分：
在 Skynet.Extras 提供 `PrometheusMetricsExporter`，将 `ActorMetricsCollector` 的快照以 Prometheus
text exposition format（v0.0.4）通过内置 HTTP 端点（HttpListener，零新依赖）暴露为 `/metrics`。

## 子任务
- [ ] `PrometheusMetricsExporterOptions`：Host/Port/MetricsPath/InstanceLabel。
- [ ] `PrometheusMetricsExporter`：scrape 时拉取快照并渲染文本格式（counter/gauge 类型、label 转义）。
- [ ] 暴露指标：per-actor 队列长度、处理总数、异常总数、平均处理耗时、uptime；系统级 send enqueue 失败计数、托管内存、进程 uptime。
- [ ] 单元测试：渲染格式、label 转义、数值正确性。
- [ ] 集成测试：真实 HTTP 拉取 /metrics 返回 200 且包含预期序列。
- [ ] 文档：`docs/prometheus-metrics.md`。

## 验收标准
- 零第三方依赖（不引入 prometheus-net）。
- 导出格式符合 Prometheus text format 0.0.4（HELP/TYPE/样本行）。
- label 值正确转义（`\`、`"`、换行）。
- 测试全部通过。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 注册 actor 并记录处理/异常后渲染 | 各序列值正确，HELP/TYPE 齐全 |
| 2 | actor name 含 `"`/`\`/换行 | label 值被转义 |
| 3 | GET /metrics | 200 + text/plain + 预期序列 |
| 4 | StopAsync 后再次请求 | 连接被拒绝/关闭 |

## 相关文件
- `src/Skynet.Core/ActorMetricsCollector.cs`（数据源）
- `src/Skynet.Net/GateServer.cs`（HttpListener 使用先例）
- `tests/Skynet.Extras.Tests/`

## 开发记录
（待填）

## Review 意见
（待填）

## QA 记录
（待填）
