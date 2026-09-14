# Prometheus 指标导出

`Skynet.Extras` 提供 `PrometheusMetricsExporter`，把 `ActorMetricsCollector` 的运行时指标以
Prometheus text exposition format（v0.0.4）通过内置 HTTP 端点暴露给 Prometheus 抓取。
实现零第三方依赖（HttpListener），scrape 时拉取快照，无抓取时零开销。

## 快速上手

```csharp
var system = new ActorSystem();
var collector = system.Metrics; // ActorSystem 暴露的 ActorMetricsCollector

var exporter = new PrometheusMetricsExporter(collector, new PrometheusMetricsExporterOptions
{
    Host = "localhost",      // 绑定所有接口用 "+" 或 "*"
    Port = 9092,
    MetricsPath = "/metrics/",
    InstanceLabel = "node-1", // 可选：多节点共采时区分实例
});
await exporter.StartAsync();
// ... 运行
await exporter.StopAsync();
```

Prometheus 配置：

```yaml
scrape_configs:
  - job_name: skynet
    static_configs:
      - targets: ['node1:9092', 'node2:9092']
```

## 导出指标

| 指标 | 类型 | 说明 |
|------|------|------|
| `skynet_actor_queue_length` | gauge | 每个 actor 的 mailbox 队列长度 |
| `skynet_actor_messages_processed_total` | counter | actor 已处理消息数 |
| `skynet_actor_exceptions_total` | counter | actor 消息处理异常次数 |
| `skynet_actor_avg_processing_milliseconds` | gauge | 平均消息处理耗时（毫秒） |
| `skynet_actor_uptime_seconds` | gauge | actor 注册至今秒数 |
| `skynet_actor_last_enqueue_timestamp_seconds` | gauge | 最近一次入队时间（Unix 秒） |
| `skynet_actor_last_processed_timestamp_seconds` | gauge | 最近一次处理完成时间（Unix 秒） |
| `skynet_send_enqueue_failures_total` | counter | 全系统 send 入队失败次数（send 不抛异常，这是唯一观测点） |
| `skynet_process_managed_memory_bytes` | gauge | 托管内存字节数 |
| `skynet_process_uptime_seconds` | gauge | 进程/导出器运行秒数 |

Per-actor 序列的标签：`handle`、`name`（未命名为空串）、`type`（实现类型短名），以及可选 `instance`。

## 自托管渲染

若希望集成进应用自有的 HTTP 服务（如 ASP.NET Core）而不使用内置监听器，直接调用渲染方法：

```csharp
string body = exporter.RenderMetrics(); // Prometheus text format
```

## 注意事项

- 抓取请求被顺序处理，对 Prometheus 默认 15-60s 的抓取间隔绰绰有余；高并发抓取场景请自托管渲染。
- label 值按规范转义（`\`、`"`、换行）。
- 端口占用时 `StartAsync` 抛出异常；`/metrics` 之外的路径返回 404，非 GET 返回 405。
