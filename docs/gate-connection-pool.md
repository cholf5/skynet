# Gate 出站连接池（Gate Connection Pool）

填补 Skynet 只有入站
`GateServer`、没有出站连接管理的空白：一个服务进程通过它同时连接多个 Gate 进程，
断线自动指数退避重连，并以 round-robin 在所有存活 Gate 之间分发出站消息。

代码位置：`src/Skynet.Net/Gate/`；测试：`tests/Skynet.Net.Tests/GateConnectionPoolTests.cs`。

## 组件一览

| 类型 | 职责 |
| --- | --- |
| `GateClientManager` | 连接池：Add/Update/Remove endpoint（幂等、同名换址自动替换连接）、round-robin 选路、统一 Start/Stop |
| `GateClient` | 单 endpoint 的连接循环：`ConnectWithRetriesAsync`、远端断线自动重连、重试耗尽停在 `Disconnected` |
| `GateProxy` | 无锁热替换的连接句柄：`Volatile.Read` / `Interlocked.Exchange`，发送方永不因重连阻塞 |
| `GateEndpoint` | `Name` + `Address`（host:port）；Name 是池内 key |
| `ReconnectPolicy` | maxAttempts / initialDelay / maxDelay / backoffFactor，参数校验 |
| `IReconnectDelayStrategy` | 重连延迟注入点（`TimerReconnectDelayStrategy` 默认 / `NoopReconnectDelayStrategy` 测试用） |
| `IGateClientTransport` | 连接建立抽象；`TcpGateClientTransport` 为 TCP 实现 |
| `IGateProxyConnection` | 单条连接抽象；payload 为不透明 `ReadOnlyMemory<byte>`，入站帧经 `FrameReceived` 事件送出 |

## 与 Skynet.Cluster TcpTransport 的关系

两者分层不同、可共存：

- `Skynet.Cluster.TcpTransport`：节点间出站连接，按 **node 路由**（node name → endpoint registry）。
- `GateClientManager`：面向 **多 endpoint 的会话级连接池**（service → N 个 Gate 进程，round-robin 挑一条活连接）。

连接池放在 `Skynet.Net`（与入站 `GateServer` 同层），不依赖 Cluster；通过
`IGateClientTransport` 抽象建立连接，也可对接 Cluster 的传输。

## 线上格式

`TcpGateClientTransport` / `TcpGateProxyConnection` 使用与入站 `GateServer` TCP 通道相同的
帧格式 `[4 字节大端长度][payload]`，因此池可直接连接一个 Skynet `GateServer`。
payload 内容对池完全不透明；协议（opcode、MessagePack/MessagePack-CSharp 编解码等）由上层自行处理。

## 用法示例

```csharp
using Skynet.Net;

var transport = new TcpGateClientTransport();
var manager = new GateClientManager(
    endpoints: ["10.0.0.1:7001", "10.0.0.2:7001"],
    transport: transport,
    reconnectPolicy: new ReconnectPolicy(
        maxAttempts: 10,
        initialDelay: TimeSpan.FromSeconds(1),
        maxDelay: TimeSpan.FromSeconds(30),
        backoffFactor: 2));

await manager.StartAsync();   // 不可达 endpoint 不阻塞启动，各自进入重连循环

// round-robin 发送；全部断线时抛 InvalidOperationException
await manager.SendThroughAnyAsync(payload);

// 或自行选路
if (manager.SelectConnectedProxy() is { } proxy)
{
    await proxy.SendAsync(payload);
    proxy.ConnectionId;  // 当前连接标识
}

// 接收入站帧（例如 Gate 推送）：订阅每条新连接（含每次重连成功）以挂接收泵。
// 见下文 client.Connected 示例。

// 运行时服务发现：动态增删 endpoint
await manager.AddGateAsync(new GateEndpoint("Gate-A", "10.0.0.3:7001"));
await manager.RemoveGateAsync("Gate-A");

await manager.StopAsync();
```

订阅每条新连接（含每次重连成功）以挂接收泵：

```csharp
client.Connected += (_, e) =>
{
    // e.Connection 是刚安装的 IGateProxyConnection
    e.Connection.FrameReceived += (__, frame) => HandleFrame(frame.Payload);
};
```

## 行为语义（验收口径）

- **不可达 endpoint 不阻塞启动**：`StartAsync` 在每个 endpoint 的首次连接尝试
  调度后即返回；失败进入重连循环。
- **远端断线自动重连并替换连接**：旧连接被 dispose，`GateProxy` 原子换成新连接，
  发送方观察到的只有 `ConnectionId` 变化。
- **重试耗尽停在 `Disconnected`**：`LastError` 保留最后一次异常；之后可再 `StartAsync` 复活。
- **round-robin 只选 `Connected`**：从游标处扫一圈，空池或全断返回 `null`，不死循环。
- **endpoint 地址变更替换连接**：`AddGateAsync`/`UpdateGateAsync` 同名同址幂等；
  同名异址停旧连新。
- **Stop 语义**：取消挂起的重连延迟；Stop 后到达的连接被立即 dispose（防僵尸连接）。

## 测试

`GateConnectionPoolTests`（fake transport + fake delay strategy，移植自参考实现的
重连行为测试）覆盖：正常连接、不可达不阻塞、断线重连替换、重试耗尽、Stop 取消、
重复 Start 防双循环、重试耗尽后可复活、round-robin 跳过断线、空池返回 null、
无可用 Gate 时发送抛错、换址替换连接、RemoveGate、`Connected` 事件逐次触发，
以及一条针对真实 `GateServer` 的 TCP 端到端回环测试。
