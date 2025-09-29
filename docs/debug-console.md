# DebugConsole 与指标采集

`Skynet.Extras` 提供一个轻量级的 TCP 调试控制台，方便在不依赖外部监控系统的情况下快速查看 Actor 运行状态、队列长度以及异常统计。本指南介绍控制台的启动方式、命令列表以及安全建议。

## 启动控制台

```bash
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --debug-console
```

默认监听 `127.0.0.1:4015`，可通过 `DebugConsoleOptions` 自定义主机地址、端口、连接 backlog 与可选的访问令牌：

```csharp
var gateway = new ActorSystemDebugConsoleGateway(system);
var options = new DebugConsoleOptions
{
        Host = "0.0.0.0",
        Port = 4100,
        AccessToken = "s3cr3t",
        IdleTimeout = TimeSpan.FromMinutes(5)
};

await using var console = new DebugConsoleServer(gateway, options);
await console.StartAsync();
```

示例通过 `telnet` 或 `nc` 连接：

```bash
telnet 127.0.0.1 4015
# 或
nc 127.0.0.1 4015
```

若配置了 `AccessToken`，首次连接需执行 `auth <token>` 进行鉴权。

## 命令列表

| 命令 | 说明 |
| --- | --- |
| `help` | 显示可用命令及使用说明 |
| `list` | 输出所有 Actor 的 handle、名称、队列长度、累计处理次数、异常数与 trace 状态 |
| `info <id|name>` | 显示指定 Actor 的详细指标快照（最后入队/处理时间、平均耗时等） |
| `trace <id|name> [on|off]` | 切换或明确开启/关闭指定 Actor 的 trace 记录，在 Actor 日志中输出前后处理日志 |
| `kill <id|name>` | 请求停止指定 Actor（等价于调用 `ActorSystem.KillAsync`） |
| `exit` | 关闭当前控制台连接 |

所有指标均来自 `ActorMetricsCollector`，为本地实时快照。`list` 命令不会跨节点聚合数据，如需集群级监控可结合 Prometheus/OTLP 在上层整合。

## 指标说明

- **Queue Length**：当前邮箱队列长度（发送 enqueue +1，Actor 读取 dequeue -1）。
- **Processed**：累计处理消息数（包含成功与异常）。
- **Exceptions**：执行 `ReceiveAsync` 抛出的异常次数。
- **Average Processing**：处理单条消息的平均耗时（毫秒），基于累计 ticks 计算。
- **Trace**：是否开启 trace 日志。开启后 ActorHost 会在处理前后输出 Info/Warning/Error 日志。

## 安全与部署建议

- 调试控制台默认为本地回环地址，仅用于开发环境。
- 部署到生产环境时必须：
  - 绑定到内网地址或通过防火墙限制访问；
  - 配置强随机的 `AccessToken` 并使用 TLS 隧道/SSH 转发；
  - 根据需要将控制台封装到运维专用微服务，避免直接暴露。
- Idle 超时时间默认为 15 分钟，可按需缩短，防止连接长时间占用资源。

## 与 ActorMetricsCollector 的关系

`ActorMetricsCollector` 在 ActorHost 层捕获以下事件：

- `OnMessageEnqueued` / `OnMessageDequeued`：维护队列长度与最后入队时间；
- `OnMessageProcessed`：累计处理次数、异常次数与平均耗时；
- `EnableTrace` / `DisableTrace`：动态打开或关闭 Actor trace 日志。

控制台通过 `IDebugConsoleActorGateway` 访问这些快照，因此也可在业务侧替换为自定义实现（例如导出至 Prometheus、Grafana 或企业内部监控平台）。
