# E-2 Prometheus 指标导出插件

## 状态
- 列：Done
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
- 新增 `PrometheusMetricsExporter`（HttpListener，零第三方依赖）+ `PrometheusMetricsExporterOptions`；
  `RenderMetrics()` 公开，可嵌入应用自有 HTTP 端点自托管。
- 指标覆盖 PRD 12.1 的 actor 四项 + uptime/最近活动时间戳，及系统级 send 失败计数、托管内存、进程 uptime。
- 开发中修复两个自发现 bug：空 instance 标签时 `AsSpan(1)` 越界（重构为无标签省略花括号的 `Series()` 辅助）；
  系统级序列误带尾逗号 `{instance="x",}`（非法格式，改为 `TrimEnd(',')`）。
- 测试 16/16 通过（含真实 HTTP 端到端抓取、404/405、重复 Start 拒绝、label 转义）。

## Review 意见
- 审查确认：抓取时拉取快照（无抓取零开销）；监听循环关闭路径依赖 StopAsync 先 Cancel 再 Stop 的顺序，
  不存在告警死循环；计数器序列在 handle 唯一性保证下单调；数字固定 invariant culture；HELP/TYPE 齐全。
- HttpListener 不支持端口 0，测试用 GetFreeTcpPort 惯用法规避（与既有测试一致）。
- 建议项（未阻塞）：`request.Url` 为 null 的病态情形由 500 兜底处理，可后续细化 400。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | 注册 actor 并记录处理/异常后渲染 | ✅ RenderMetrics_ShouldEmitSeriesForRegisteredActor |
| 2 | actor name 含 `"`/`\`/换行 | ✅ RenderMetrics_ShouldEscapeLabelValues |
| 3 | GET /metrics 返回 200 + text/plain + 预期序列 | ✅ HttpEndpoint_ShouldServeMetrics |
| 4 | StopAsync 后行为 | ✅ StartTwice_ShouldThrow + 404/405 用例 |
| 5 | 选项校验 | ✅ Options_ShouldRejectInvalidValues |
| 6 | 无 instance 标签不产生空花括号 | ✅ RenderMetrics_WithoutInstanceLabel_ShouldOmitLabelBracesOnSystemSeries |

无 P0/P1 问题。**QA Passed**
