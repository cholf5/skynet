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

## Wire 协议（MessageEnvelope）

跨节点投递时，`MessageEnvelope` 由 `MessageEnvelopeSerializer` 序列化为 `SerializedMessageEnvelope`
（MessagePack，按 Key 序数组编码）：

| Key | 字段 | 说明 |
|-----|------|------|
| 0-3 | `MessageId`、`From`、`To`、`CallType` | 消息标识与路由元数据 |
| 4 | `PayloadContractId`（int） | payload 类型的稳定合约 id；`0` 保留给 `null` payload |
| 5 | `Payload`（byte[]） | MessagePack 编码后的 payload |
| 6-8 | `TraceId`、`Timestamp`、`TimeToLiveTicks` | 追踪与生命周期 |
| 9 | `Version` | wire 协议版本，当前为 **2** |

### Contract id 分配规则
- contract id 由 payload 类型 `Type.FullName` 的 **FNV-1a 32 位哈希**（UTF-8 字节）计算得出。
- id 只依赖类型全名字符串，因此两个节点即使编译顺序、程序集加载顺序不同，对同一类型也会得到相同 id，
  天然保证跨节点一致性。
- `PayloadContractRegistry.NullPayloadContractId`（0）保留给 `null` payload；真实类型的哈希若命中 0
  （概率约 2^-32），将以 `全名#1`、`全名#2` … 确定性重哈希，各节点仍得到一致结果。
- **冲突检测**：注册时若同一 id 已被不同 FullName 占用，`PayloadContractRegistry.Register` 会抛出
  异常（节点启动初期即可暴露）；同一类型（或同一 FullName）重复注册是幂等的。
- 常用基元类型（`string`、数值类型、`Guid`、`byte[]` 等）与 `EmptyPayload` 由 Core 预注册。

### 类型解析（禁止运行时反射）
- 序列化（发送端）：payload 类型已注册则直接使用其 id；未注册则计算哈希并**自注册**（发送端本就持有
  具体类型，不涉及字符串反查）。`null` payload 写 contract id 0。
- 反序列化（接收端）：严格查 `PayloadContractRegistry` 映射表（`contractId → Type`），**不允许**
  `Type.GetType` 等字符串反查；未知 id 抛出 `UnknownPayloadContractException`（消息中含 contract id
  与修复提示）。`TcpTransport` 收到此类帧时记录 Error 日志并丢弃该帧，但**连接保持存活**。
- `[SkynetActor]` 合约的请求/响应 payload 类型由 `SkynetActorGenerator` 在编译期生成
  `PayloadContractRegistry.Register<T>()` 注册代码（ModuleInitializer 模式），因此使用生成器合约的
  节点无需手动注册。非合约类型的普通 payload 若要在远端解析，需在接收节点显式
  `PayloadContractRegistry.Register<T>()`。

### 版本兼容
- wire 协议版本 2（本版本）开始使用 contract id；版本 1（字符串 `PayloadType` /
  AssemblyQualifiedName）**不再支持**——反序列化到 v1 帧会抛出 `NotSupportedException`，提示全集群
  统一升级。这是一个 breaking change，所有节点必须同步升级。
- 本地 short-circuit 路径不经过序列化（payload 直接传递对象引用），不受该变更影响。

### RPC 契约返回类型约束（breaking change）
- `[SkynetActor]` 合约接口的方法返回类型只允许 `Task`、`Task<T>`、`ValueTask`、`ValueTask<T>`
  （call 语义，用 `await` 等待结果）以及 `void`（send 语义）。**同步返回类型不再支持**——
  含同步返回方法的合约接口会报编译期诊断 **SKY007**（Error 级）。
- 背景：skynet 的 `skynet.call` 是协程阻塞，C# 的等价表达是 `await`，线程阻塞
  （`.GetAwaiter().GetResult()`）在 Actor 消息处理内调用 proxy 时会直接死锁。
- 迁移方式：将 `T Method(...)` 改为 `Task<T> MethodAsync(...)` 或 `ValueTask<T> MethodAsync(...)`
  （实现侧用 `Task.FromResult` / `ValueTask.FromResult` 包装即可）；单向消息改为 `void` 返回，
  生成 proxy 对 `void` 方法是真正的 fire-and-forget——入队即返回，不等待 actor 处理完成，
  入队失败异常通过 `OnlyOnFaulted` 续接观察，不会产生未观察 Task 异常。
- 生成代码中不再出现任何 `.GetAwaiter().GetResult()` 同步阻塞形态。

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

