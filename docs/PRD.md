# PRD — **Skynet (C#)** — 极简 Skynet 风格 Actor 框架（面向游戏后端，C# 实现）

> 目的：交付一个**功能层面上尽量还原 Skynet**（cloudwu/skynet）语义、但在接口层采用 **C# 强类型 + Interface + SourceGenerator + MessagePack** 的轻量 Actor 框架。核心原则是 **极简内核、插件化扩展、把复杂性留给用户**（而不是强制框架行为）。
> 目标交付物是一个可开源的 GitHub 仓库 + 可发布到 NuGet 的组件集合，含样例服务与压测脚本，能被编程 Agent 自动实现。

---

# 1. 总体目标与非目标

## 1.1 目标

* 在功能层面尽可能还原 Skynet（服务 handle/name、send/call、session、uniqueservice、Cluster、Gate/MsgServer、Multicast、DebugConsole、DataCenter 等）；
* 在接口层提供 C# 开发体验：Interface + SourceGenerator 自动生成 proxy/stub、MessagePack 用作默认序列化；
* 保持框架极简：**默认不做持久化/激活回收/幂等控制**，提供钩子/插件以便用户按需启用；
* 支持单进程与跨进程 Cluster（同语言 C# 节点间 RPC）；
* 提供样例实现（Login、Echo、Gate/Session、Cluster sample）和压测脚本，供迁移与性能评估。

## 1.2 非目标

* 不实现框架级别的状态持久化、自动激活/回收策略或分布式事务（这些作为插件/样例由用户负责）；
* 不作为跨语言 RPC 框架（仅支持 C# 节点间、以及外部客户端通过 Gate/HTTP/WebSocket）；
* 不实现复杂的 service mesh、gossip 或一致性协议（仅提供 pluggable registry，默认可用 Redis 或静态配置）。

---

# 2. 设计原则与约束

* **极简内核**：最小化默认运行时行为，仅保留必须功能（mailbox、actor lifecycle、transport abstraction、registry、call/send semantics、SourceGen integration）。
* **透明调用语义**：对用户，`send`/`call` 与 Skynet 语义一致（本地/远程对用户透明），但框架内部可 short-circuit 用于优化。
* **强制可插拔**：任何框架认为“可能会引发争议或影响性能”的功能（持久化、激活回收、幂等、复杂监控）均作为可选插件/中间件实现，不启用则零开销。
* **类型优先**：首选 Interface + SourceGenerator 方案，自动生成 proxy/stub 与序列化代码，保证 IDE/编译器保护。
* **兼容 Skynet 用法**：提供 handle（数字）与 name（字符串）双向映射接口，并支持 uniqueservice 与简单 multicast/room 功能。
* **安全与可观察**：默认附带基本的日志、metric、trace hook、DebugConsole，便于运维与排查。

---

# 3. 目标用户群与典型用例

## 3.1 目标用户

* 熟悉 Skynet/Actor 模型的游戏后端开发团队，想把现有 Lua/Skynet 心智模型迁移到 C# 强类型生态；
* 中小团队需快速开发区服级别游戏后端，重视可控性与调试可行性。

## 3.2 典型用例

* 登录认证服务（LoginActor）：客户端发起 login 请求→ Gate 转发到 LoginActor → LoginActor 调用 DB Actor 验证 → 返回结果。
* 房间/房间管理（RoomActor + Multicast）：多个玩家加入同一 RoomActor，广播消息到该房间成员。
* 网关/会话管理（GateServer + SessionActor）：TCP/WebSocket 连接接入，绑定到 SessionActor，服务器 Actor 向该 SessionActor 发反向消息。
* 全服唯一服务（DataCenter 或 UniqueService）：某些全局资源由 uniqueservice 承担（单例）。

---

# 4. 功能需求（详细）

以下按模块列出必须实现（MUST）与可选（OPTIONAL）功能。

## 4.1 核心运行时（MUST）

* Actor 基类 `Actor`：封装 Channel mailbox，提供 `Tell(Func<Task>)` / `Ask<T>(Func<Task<T>>)`。
* ActorSystem / ActorHost：启动时加载/注册 Actor，实现本地 registry（handle ↔ name 映射）。
* 生命周期 API：`CreateService(name?)`、`GetByHandle(handle)`、`GetByName(name)`、`Kill(handle)`、`ListServices()`。
* Handle 管理：分配全局唯一数字 handle（进程内自增或基于 pool）。
* Message metadata envelope（必须携带MessageId、TraceId、CallType、FromHandle、ToHandle、Timestamp、TTL、Version）。
* send/call 语义：

  * `send`：fire-and-forget（发送成功即返回）；如果跨节点，发送层保证投递到目标节点的 transport 层（非业务级 ack）。
  * `call`：返回 `Task<T>`，在远程节点上执行后返回结果或超时异常。
* 本地/远程 short-circuit（默认开启）：本地调用直接入本地 mailbox（无序列化）；远程调用序列化并通过 transport 发送。 配置可关闭以获得完全一样的行为（用于 debug/一致性验证）。

## 4.2 SourceGenerator + Interface RPC（MUST）

* 支持用户声明 interface（例如 `ILoginActor`），运行时/SourceGenerator 生成 `ILoginActorProxy`：在调用端把方法调用打包成 MessagePack envelope 并走 ActorSystem transport；在服务器端接收并反序列化为具体 method 调用并 `Ask`/`Tell` 到目标 Actor。
* 生成代码应避免 runtime reflection（除非在 debug 模式），并支持 MessagePack 属性注解。

## 4.3 Transport 抽象（MUST）

* Transport 接口（发送/接收 bytes、连接管理、heartbeat/callback）：支持至少两种实现：`InProc`（开发测试）和 `Tcp`（生产）；Transport 层负责 framing（长度前缀）、envelope encryption option、compression option。
* Cluster RPC：Transport 与 Registry 协同实现节点间路由（如果目标在本节点，short-circuit；否则通过 TCP 发往目标节点）。

## 4.4 Cluster / Registry（MUST）

* 本地 registry + cluster registry abstraction（pluggable）。默认实现：静态 config + optional Redis registry（供节点 discovery）；实现支持 mapping：`actorId / namespace -> nodeId`。
* Routing：支持：

  * direct address routing（actorHandle 唯一映射到 node）；
  * consistent hashing（用于分布式 actor placement，config 可选）；
  * named service lookup（`GetByName` 返回 handle 或 node）。
* uniqueservice：通过 registry 协调（client will query registry for which node has unique; simple lock via Redis SETNX or consul-like registration).

## 4.5 GateServer / Session / MsgServer（MUST）

* GateServer: 管理客户端 TCP/WebSocket 连接，生成 connectionId/sessionId，绑定到 SessionActor（或用户自定义 actor）。
* SessionActor: 以 actor 形式表示单个连接，可接收来自内部 Actor 的消息并把其写回 socket。
* MsgServer: 提供上层业务路由（如按 region/game server 分发），可与 GateServer 协作。

## 4.6 Multicast / Group（MUST）

* Group/Room 管理：提供 `RoomManager`（actor or component）管理 room membership，支持 `BroadcastToRoom(roomId, message)` 高效广播（实现可基于 membership table + batched sends）。
* 提供 membership API：`JoinRoom`, `LeaveRoom`, `GetMembers`.

## 4.7 DebugConsole / CLI（MUST）

* Telnet or HTTP debug console：列出 services、handle -> name、mailbox queue length、actor metrics、trigger GC、查看 logs。
* Console 命令：`list`, `info <handle>`, `trace <handle>`, `kill <handle>`。

## 4.8 Observability（MUST）

* 基本 metrics：per-actor queue length, avg processing time, processed message count, exceptions.
* Tracing: Message trace id propagation in metadata (hook into user's tracing system).
* Logging: use `Microsoft.Extensions.Logging` abstractions.

## 4.9 Plugins / Extensions（MUST）

* Serializer plugin point (default MessagePack).
* Registry plugin point (default static config + optional Redis sample).
* Transport plugin point (default InProc + Tcp).
* Monitoring plugin point (prometheus / pushgateway hooks example).
* Persistence hooks (optional): snapshot/wal API for user plugins (not enabled by default).

## 4.10 Security（MUST）

* Transport should support TLS (optional) for cross-node and gate-client connections.
* GateServer must support authentication hook for session admission.
* Message validation: basic header validation and max payload size.
* Rate limiting hooks at Gate level (pluggable).

---

# 5. 非功能性需求（NFRs）

## 5.1 性能与规模（目标）

* 设计支持单节点**高并发**：单节点目标能稳定处理数万~数十万消息/s（具体目标与环境相关，需后续压测确认）。
* 本地 `call`（short-circuit）延迟目标：median < 若干毫秒（取决于机器与负载），应尽可能低；远程 `call` 增加网络与序列化延迟。
* 单个 actor mailbox 处理速度：最大可达数千~数万 msg/s，视处理逻辑而定。

> 注：这些为目标范围，实际性能需通过压测做出 SLA；PRD 不承诺具体数值，Agent 在实现过程中需提供基于目标的压测报告作为验收材料。

## 5.2 可靠性

* Crash recovery: 节点重启后 registry/cluster 能重新注册节点；session 重连由 Gate + SessionActor 处理（用户层定义恢复策略）。
* Transport 可配置断线重连与 basic retries（框架负责通知失败，不自动重试幂等）。

## 5.3 可维护性

* 源代码需遵循清晰模块化、单一职责；充分的单元测试与集成测试；CI 自动构建与测试。
* SourceGenerator 生成的代码应可读、易调试（在 debug 构建输出生成代码到项目目录）。

## 5.4 可部署性

* 单二进制可运行（dotnet publish），可容器化部署（Dockerfile template 提供）。
* 配置使用环境变量或 yaml/json 文件（支持热 reload of certain config like logging level）。

---

# 6. 详细架构（组件与交互描述）

> 文字架构草图（可交给 Agent 生成图片）：
> `Client <-> GateServer (SessionActor) <-> Cluster Transport <-> ActorHost (ActorSystem) [=> Actor Instances + Registry + Plugins]`

## 6.1 主要组件

* **Actor**: mailbox + state (user managed) + lifecycle hooks (OnStart, OnStop, OnMessageError hook).
* **ActorHost / ActorSystem**: 管理 actor 实例表、handle 分配、name registry、dispatch、sourcegen integration。
* **Transport**: 负责节点间字节传输、帧解析、routing callback。
* **ClusterRegistry**: node discovery and actor placement (pluggable implementation).
* **SourceGenerator**: compile-time generator turning interface into proxy and server-side method dispatcher.
* **GateServer**: 接收客户端连接，维护 connection->SessionActor 映射。
* **DebugConsole**: 远程诊断接口（telnet/http）。

## 6.2 Call flow（远程 call）

1. Caller gets `IService` proxy via `ActorSystem.Get<T>(target)`（target 指定 handle/name）。
2. `proxy.Method(args)` => SourceGen 生成 stub，序列化 args -> build envelope with metadata (msgId, traceId, callType=Ask).
3. ActorSystem resolves target via registry (local? remote?).
4. If local and actor exists → short-circuit: de-serialize skipped, mailbox `Ask` 入队，执行并返回结果（Task）。
5. If remote -> Transport.Send(node, bytes)。远端 ActorSystem 接收 bytes -> 反序列化，派发到目标 actor 的 mailbox，执行并将结果打包返回到调用方节点（或通过 Transport 转发 back）。
6. Caller收到返回 envelope -> stub 解包 -> set Task result。

---

# 7. API 细节与开发者体验（示例与规范）

## 7.1 Service 定义（开发者代码）

```csharp
// user defines interface
public interface ILoginActor
{
    Task<LoginResult> LoginAsync(LoginRequest req);
    Task HeartbeatAsync(Heartbeat req);
}

// annotate implementation for auto discovery (optional)
[SkynetService("login", Unique = false)]
public class LoginActor : Actor, ILoginActor
{
    public Task<LoginResult> LoginAsync(LoginRequest req) { /* business */ }
    public Task HeartbeatAsync(Heartbeat req) { /* business */ }
}
```

## 7.2 获取 Actor（API）

```csharp
// by name (string) - returns proxy implementing interface
var login = ActorSystem.GetActor<ILoginActor>("login"); 

// by handle (numeric)
var loginByHandle = ActorSystem.GetActor<ILoginActor>(handle);

// send (fire-and-forget)
login.HeartbeatAsync(new Heartbeat{Ts = DateTime.UtcNow}); // proxy maps to send/tell

// call (ask)
var res = await login.LoginAsync(new LoginRequest{Account="u1"});
```

## 7.3 Actor 基类（重要方法）

```csharp
public abstract class Actor
{
    protected Task<T> Ask<T>(Func<Task<T>> func); // often used inside generated server dispatch
    protected void Tell(Func<Task> action);        // for internal enqueuing
    public virtual Task OnStartAsync() => Task.CompletedTask;
    public virtual Task OnStopAsync() => Task.CompletedTask;
}
```

## 7.4 Message Envelope（MessagePack schema - 伪结构）

* Envelope fields (binary MessagePack map or array):

  * version: int
  * msgId: guid/string
  * traceId: string (optional)
  * callType: byte (0=tell,1=ask,2=response,3=error)
  * fromHandle: long
  * toHandle: long
  * methodName: string (for interface dispatch)
  * payload: bytes (MessagePack of arguments or return value)
  * ttl: optional

> SourceGenerator 与 server dispatch 将 methodName 与 payload 解耦，避免反射查找。

---

# 8. SourceGenerator 规范（必须非常清楚）

## 8.1 输入约定（开发者）

* Any interface decorated with `[SkynetRpc]` (or all interfaces in `Skylark` project) will have proxy generated.
* Methods may have `CancellationToken` parameter (last position) — generator will support cancellation.

## 8.2 生成内容

* **Client proxy**: implements the interface, for each method: create MessagePack payload from args, add header metadata, call `ActorSystem.SendAsync(target, envelope, expectResponse)`; if expected response -> await the `TaskCompletionSource` keyed by msgId.
* **Server dispatcher**: map methodName => strongly typed call; when request arrives, call `actor.Ask(() => impl.Method(args))` and serialize return to response payload.
* **Type safety**: generator must produce code that serializes/deserializes without using runtime reflection (call MessagePack generated formatters or use pre-registered resolvers).
* **Error handling**: exceptions in server should be captured and returned as `error` envelope; client stub should translate to exception.

## 8.3 Backward/Versioning support

* Methods should bear a generated version token; additive changes allowed if payload uses optional fields. Generator should produce warnings if method signature changes in incompatible way.

---

# 9. Transport & Framing（具体协议建议）

## 9.1 Framing

* Length-prefixed messages (4 or 8 byte unsigned big-endian length) followed by the MessagePack envelope bytes.

## 9.2 Header fields (binary)

* byte protocolVersion
* byte flags (compression, encrypted)
* 16 bytes traceId/guid (optional)
* rest: payload length -> payload.

## 9.3 Reliability

* Transport must provide callbacks for `SendComplete` / `SendFailed` / `Receive` and detect remote node dead (heartbeat). It **not** responsible for at-least-once/more advanced semantics — leave to application.

---

# 10. Cluster registry & placement（细则）

## 10.1 Registry abstraction

```csharp
public interface IClusterRegistry
{
    Task RegisterNodeAsync(NodeInfo node);
    Task UnregisterNodeAsync(NodeId id);
    Task<NodeInfo[]> ListNodesAsync();
    Task<NodeInfo> ResolveActorHandleAsync(long handle);
    Task<long?> LookupUniqueServiceAsync(string name);
}
```

## 10.2 Default implementations

* **StaticConfigRegistry**: read nodes from config / env, used for simplest deployments.
* **RedisRegistry**: optional sample - registers node info to redis; supports SETNX for unique services.

## 10.3 Placement strategies

* **Direct**: caller resolves target handle -> node.
* **ConsistentHash**: for auto-placement for some actor namespaces.
* **Manual**: user can create actor on specific node.

---

# 11. Gate / Session / MsgServer 设计细节

## 11.1 GateServer

* Accept clients via TCP or WebSocket.
* For each accepted connection, create a `SessionActor` (or let user choose) and hand raw bytes / messages to it.
* SessionActor exposes API for other actors: `SendToConnection(connectionId, message)`.

## 11.2 Session lifecycle

* SessionActor OnStart binds connectionId; OnStop cleans up binding.
* Reconnect strategy: GateServer optional support to re-bind sessionId with replaced connection (user handles authentication/authorization).

## 11.3 MsgServer

* Works above GateServer: business routing layer that finds appropriate game server actor and forward client messages.

---

# 12. Observability、Debugging、Monitoring（细则）

## 12.1 Metrics

* Expose: `actor_mailbox_queue_length`, `actor_avg_processing_ms`, `actor_exception_count`, `messages_sent`, `messages_received`, `transport_latency_histogram`, `transport_errors`.

## 12.2 Tracing

* Propagate `traceId` header across messages. Provide a hook so user can integrate with OpenTelemetry.

## 12.3 Debug Console

* Implement simple telnet REPL and HTTP API for queries:

  * `list services`
  * `info <handle>`
  * `queue <handle>`
  * `dump <handle> stack` (if available)
  * `invoke <handle> <method> <json-args>` (testing aid)

---

# 13. Testing / QA 要求（Agent 执行清单）

## 13.1 单元测试（每个模块）

* Mailbox ordering guarantees; ensure `Ask`/`Tell` sequencing.
* ActorHost registry operations.
* SourceGenerator generated proxies correctness (roundtrip tests).
* Transport framing tests (send/receive fragments).
* Registry resolution tests (static + redis mock).

## 13.2 集成测试（必做）

* Local inproc: create actor A & B, call from A to B (short-circuit path).
* Cross-process: spawn two nodes, register actor in nodeB, call from nodeA to nodeB (remote path).
* GateSession flow: client -> gate -> login actor -> db actor -> response.
* Multicast: room broadcast to N session actors, verify delivery.

## 13.3 性能 & Chaos 测试

* Baseline throughput/latency for short-circuit call with no-op actor (to measure overhead).
* Cross-node call performance under various message sizes.
* Chaos tests: node kill + restart, network packet loss simulation (Transport layer supports injecting delays/drop).
* Hotspot tests: single actor receiving extremely high QPS; observe saturation and queue metrics.

## 13.4 Acceptance criteria

* All unit & integration tests green.
* Short-circuit call median latency within expected baseline (baseline to be measured and recorded by Agent).
* Remote calls succeed under cluster with no message corruption and proper error handling on node failure.
* Debug console commands functioning.
* CI builds producing NuGet packages.

---

# 14. Packaging、CI/CD、Repo 约定（Agent 执行）

## 14.1 Repo structure（建议）

```
/Skynet
  /src
    /Skynet.Core
    /Skynet.Msgpack
    /Skynet.SourceGen
    /Skynet.Cluster
    /Skynet.Samples
  /tests
  /docs
  /benchmarks
  /scripts (docker, launch scripts)
  README.md
  LICENSE (MIT)
```

## 14.2 CI (GitHub Actions)

* Build & test on matrix (windows/linux), run unit tests.
* Lint & format checks (dotnet format).
* Publish artifacts on release: NuGet package + docker images for sample servers.

## 14.3 Release

* Semantic versioning; major/minor/patch.
* Release notes should list breaking changes, migration hints.

---

# 15. Roadmap / Milestones (技术拆分，以可交付模块为单位)

> 下面以里程碑（Milestone）划分，不给时间估计，由 Agent 按优先级实现并提交 PR。每个里程碑交付物需要满足相应 Acceptance Criteria。

## Milestone 1 — Core Runtime (MUST)

* Mailbox (Channel) & Actor base.
* ActorHost / local registry (handle/name).
* send/call semantics (short-circuit local).
* InProc transport (for testing).
* Unit tests for mailbox/order.
* Sample Echo service + README run guide.
  **Acceptance**: local echo example run; tests green.

## Milestone 2 — SourceGenerator & MessagePack (MUST)

* Implement SourceGenerator that generates proxies and server-side dispatch.
* Integrate MessagePack default serializer.
* Example ILoginActor with proxy generated.
  **Acceptance**: generated proxy code compiles; roundtrip call (local + remote via inproc transport) works.

## Milestone 3 — TCP Transport & Cross-node RPC (MUST)

* Implement TCP transport with framing & cluster registry abstraction.
* Static config registry.
* Cross-process example: nodeA -> nodeB call.
  **Acceptance**: remote call success; debug console shows nodes; tests for transport framing pass.

## Milestone 4 — GateServer / Session / MsgServer (MUST)

* WebSocket/TCP gate with SessionActor mapping.
* SessionActor example; client sample using simple TCP client.
  **Acceptance**: client connects -> login via Gate -> login actor -> response.

## Milestone 5 — Multicast / Room / Debug Console (MUST)

* Room manager + broadcast API.
* Debug console (telnet/http).
  **Acceptance**: room broadcast to 100 sessions works; debug console commands functional.

## Milestone 6 — Cluster Registry Plugins / Redis (OPTIONAL)

* Redis registry implementation for node discovery and simple unique service support.
  **Acceptance**: registry can register/unregister nodes and resolve actors.

## Milestone 7 — Observability & Benchmarks

* Metrics, traceId propagation, basic Prometheus exporter sample, benchmark scripts.
  **Acceptance**: benchmark produced and baseline metrics logged.

---

# 16. Risks / Trade-offs / Mitigations

## Risk: Performance overhead of SourceGen + MessagePack vs hand-optimized RPC

* Mitigation: Measure baseline; optimize generated code; support direct inproc short-circuit path to avoid serialization when local.

## Risk: Users expect framework to do persistence/activation (Orleans-style)

* Mitigation: Document design decision clearly; provide plugin interface + sample persistent actor (as optional) for those who want it.

## Risk: Name collision/legal around “Skynet”

* You chose “Skynet”. Add clear MIT license and readme noting it’s not affiliated; ensure repo name availability. (User confirmed choice.)

## Risk: Complexity of cluster placement & failure modes

* Mitigation: Start with static registry; provide Redis sample; avoid complex consensus algorithms in v1.

---

# 17. Security Considerations

* Transport TLS support mandatory for cross-node in production; provide config knobs.
* Gate should implement authentication hook—framework only exposes the hook.
* Prevent oversized payloads; validate headers and TTL; drop malformed frames.
* Limit debug console exposure (bind to localhost by default; require auth).

---

# 18. Deliverables for the programming Agent (具体任务清单)

Produce PRs (one feature per PR) with tests and docs. For each PR include:

* Code implementing feature.
* Unit tests & minimal integration tests.
* README with how to run locally & sample usage.
* Benchmark script if performance relevant.
* Example: PR for Milestone 1 must include: `Skynet.Core` with Actor & ActorSystem, unit tests, Echo sample and README.

具体任务（按优先级）：

1. Repo skeleton + CI + LICENSE + CODE_OF_CONDUCT + CONTRIBUTING.md.
2. Implement Actor base + mailbox + ActorHost + local registry + unit tests.
3. SourceGenerator skeleton (generate proxy + dispatcher) + MessagePack integration + tests.
4. InProc transport & sample roundtrip test.
5. TCP transport + framing + static registry + cross-process sample.
6. GateServer (TCP + WebSocket) + SessionActor + client sample.
7. RoomManager / Multicast sample.
8. DebugConsole and basic metrics exposure.
9. Redis registry plugin (optional).
10. Packaging (NuGet) and sample dockerfiles.

---

# 19. Acceptance Criteria（总结性）

项目可被认为最小可交付（MVP）：

* 能在单机运行 `SkynetHost`：启动 actor, 使用 `ActorSystem.Get<T>("name")` 进行 `send`/`call` 并得到正确响应。
* SourceGenerator 正常工作：interface -> client proxy -> server dispatch。
* Support cross-node call via TCP transport with simple registry.
* GateServer 能接收客户端连接并把消息转为 SessionActor 事件。
* 有基本监控（队列长度、处理耗时）与 debug console。
* CI 绿色、README 指南完整、样例可运行、单元与集成测试覆盖核心路径。

---

# 20. Appendix — 关键实现示例（片段）

## 20.1 Actor base（伪代码）

```csharp
public abstract class Actor
{
    private Channel<Func<Task>> _mailbox = Channel.CreateUnbounded<Func<Task>>();
    private int _running = 0;

    protected async Task Enqueue(Func<Task> action)
    {
        await _mailbox.Writer.WriteAsync(action);
        TryStartPump();
    }

    public void Tell(Func<Task> action) => _ = Enqueue(action);

    public Task<T> Ask<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Enqueue(async () =>
        {
            try { tcs.SetResult(await func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
```

## 20.2 Envelope (MessagePack usage - pseudocode)

```csharp
[MessagePackObject]
public class Envelope
{
    [Key(0)] public int Version { get; set; }
    [Key(1)] public Guid MsgId { get; set; }
    [Key(2)] public string TraceId { get; set; }
    [Key(3)] public CallType CallType { get; set; }
    [Key(4)] public long From { get; set; }
    [Key(5)] public long To { get; set; }
    [Key(6)] public string Method { get; set; }
    [Key(7)] public byte[] Payload { get; set; }
}
```

## 20.3 Example Interface + usage

```csharp
public interface ILoginActor
{
    Task<LoginResult> LoginAsync(LoginRequest req);
}

var login = ActorSystem.GetActor<ILoginActor>("login");
var r = await login.LoginAsync(new LoginRequest{Account="u1"});
```

---

# 21. 下一步（直接行动清单：Agent 要做的第一批任务 — 直接开始）

1. 在 GitHub 建 Repo `Skynet` 或 `SkynetSharp`（你已决定 `Skynet` 名称即可）。初始化 MIT LICENSE、README stub、.gitignore。
2. 提交 Milestone 1 PR：实现 Actor base、ActorHost、本地 registry（handle/name）、InProc transport、Echo sample、单元测试、CI（build+test）。PR 需包含 README 中如何运行示例。
3. 在 Milestone 1 PR 合并后，开始 Milestone 2（SourceGenerator + MessagePack）。