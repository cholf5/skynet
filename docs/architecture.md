# 架构设计解读

本文档介绍 Skynet 的分层结构、关键组件以及消息流转路径，帮助读者理解系统运作原理并为扩展开发提供参考。

## 总体架构
Skynet 遵循“三层一体”的设计：
1. **Core 层**：实现 Actor 生命周期、消息调度、序列化与指标采集。
2. **Transport 层**：提供多种传输实现，包含 In-Proc 与 TCP，并支持自定义扩展。
3. **Integration 层**：面向业务系统的周边能力，如会话网关、房间管理、调试工具等。

```
┌────────────────────────────────────────────┐
│                应用服务 / Actors           │
├────────────────────────────────────────────┤
│           Skynet.Core (Actor Runtime)      │
├────────────────────────────────────────────┤
│  InProc Transport  |  TCP Transport  | ... │
├────────────────────────────────────────────┤
│   Cluster Registry | Gate Server | Extras │
└────────────────────────────────────────────┘
```

## 核心组件

### ActorSystem
- 负责 Actor 的注册、创建与生命周期管理。
- 提供 `Send` 与 `CallAsync` API，确保消息按顺序投递。
- 集成 `ActorMetricsCollector`，记录处理次数、平均延迟等指标。

### Mailbox & MessageEnvelope
- 每个 Actor 维护独立的 `Channel`，避免跨 Actor 干扰。
- `MessageEnvelope` 统一封装消息体、调用类型、Trace 信息。

### Transport 抽象
- `ITransport` 描述基础投递能力，包含 `SendAsync`、`CallAsync`、`DisposeAsync` 等方法。
- `InProcTransport` 面向单进程开发，直接将消息投递到目标 Actor 的信箱中。
- `TcpTransport` 通过连接池与心跳机制实现跨进程通信，并配合 `IClusterRegistry` 定位远程 Actor。

### Cluster Registry
- `StaticClusterRegistry` 通过静态配置映射 Actor 与节点。
- `RedisClusterRegistry` 借助 Redis 存储节点信息，实现动态发现与健康检查。

### Session & GateServer
- `GateServer` 接收外部连接（TCP/WebSocket），为每个连接创建 `SessionActor`。
- `RoomManager` 与 `RoomSessionRouter` 提供房间广播、成员管理等高级功能。

## 消息流转
1. 客户端或内部服务构造消息并调用 `ActorSystem.Send/CallAsync`。
2. `ActorSystem` 根据目标 Actor 的定位信息选择本地或远程投递。
3. 选择的 Transport 将消息封装为 `MessageEnvelope` 并交由目标 Actor 的 Mailbox。
4. Actor 按序读取信箱中的消息，执行对应业务逻辑。
5. 若为 `CallAsync`，结果通过 Transport 回传给调用方。

## 可观测性
- `ActorMetricsCollector` 暴露每个 Actor 的消息吞吐与延迟统计。
- `DebugConsoleServer` 提供命令行查询接口，可获取运行时状态。
- 日志系统遵循 `Microsoft.Extensions.Logging`，可接入多种后端。

## 扩展指引
| 扩展类型 | 关键接口 | 实现要点 |
|----------|----------|----------|
| 新 Transport | `ITransport` | 实现连接管理、序列化与重试策略 |
| 自定义注册中心 | `IClusterRegistry` | 提供 Actor 定位、健康检查与负载均衡 |
| 业务 Actor | `Actor` 基类 | 重写 `OnReceiveAsync`，使用依赖注入获取服务 |
| 运维工具 | `ActorMetricsCollector`、`IDebugConsoleActorGateway` | 聚合指标，输出到仪表盘或 CLI |

## 部署拓扑
- **单进程模式**：使用 `InProcTransport`，适用于开发与单机部署。
- **多节点模式**：结合 `TcpTransport` + `StaticClusterRegistry`，适用于固定节点拓扑。
- **动态集群模式**：通过 `RedisClusterRegistry` 注册节点，实现弹性扩容。

## 性能优化建议
1. 根据业务场景调整 `ActorSystemOptions` 的 Mailbox 容量与并发策略。
2. 使用结构化日志收集处理延迟，以配合指标分析瓶颈。
3. 对于网络密集型 Actor，可拆分为多个实例并通过路由层做分片。
4. 在高负载场景下启用批量消息或合并网络调用，减少跨节点开销。

## 参考文档
- [快速上手指南](getting-started.md)
- [Redis 注册中心说明](redis-registry.md)
- [调试控制台使用指南](debug-console.md)

