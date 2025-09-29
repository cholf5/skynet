# Skynet (.NET)

> ⚠️ 该项目与 cloudwu/skynet 无官方关联，旨在探索 C# 生态下的极简 Actor 框架实现。

Skynet 是一个面向游戏后端的 Actor 框架，目标是在 .NET 生态中还原 cloudwu/skynet 的开发体验，提供强类型接口、SourceGenerator 自动化与可插拔运行时组件。本仓库遵循 [MIT 许可证](LICENSE)。

## 仓库结构

```
Skynet.sln
├── src/
│   ├── Skynet.Core/         # 核心 Actor 运行时（ActorSystem、Registry 等）
│   ├── Skynet.Cluster/      # 跨节点通信与注册表实现
│   ├── Skynet.Net/          # 传输层、TCP/WebSocket 接入
│   ├── Skynet.Extras/       # DataCenter、Multicast、DebugConsole 等扩展
│   └── Skynet.Examples/     # 示例程序与入门指南
└── tests/
    ├── Skynet.Core.Tests/
    ├── Skynet.Cluster.Tests/
    ├── Skynet.Net.Tests/
    └── Skynet.Extras.Tests/
```

## 开发环境

- .NET SDK 9.0 (preview/RC)
- 推荐使用 Visual Studio 2022 17.11+ 或 Rider 2024.2+
- 操作系统：Windows、macOS 或任意支持 .NET 9 的 Linux 发行版

> 若本地未安装 .NET 9，可执行 `./dotnet-install.sh --version 9.0.100-rc.1.24452.12 --install-dir ~/.dotnet` 并将 `~/.dotnet` 添加到 `PATH`。

## 快速开始

```bash
# 克隆仓库
git clone https://github.com/<your-org>/Skynet.git
cd Skynet

# 还原依赖并构建
dotnet build

# 执行核心测试套件
dotnet test tests/Skynet.Core.Tests/Skynet.Core.Tests.csproj

# 运行示例（含登录代理与 Echo）
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj
```

示例程序会首先演示通过 SourceGenerator 生成的 `ILoginActor` 代理，然后启动本地 `ActorSystem` 注册名为 `echo` 的服务：

```text
Bootstrapping Skynet runtime with generated login proxy...
Login => Welcome demo!
Ping => PONG: demo
Echo actor registered as 'echo'. Type messages to interact. Press ENTER on an empty line to exit.
```

按回车退出后，ActorSystem 会自动释放。

## 核心能力

- InProc Actor 运行时：基于 `System.Threading.Channels` 的邮箱，保证消息顺序与异常隔离。
- `ActorSystem` 注册与查找：支持 handle/name 双索引、唯一服务与生命周期管理。
- 消息语义：提供 `SendAsync`（fire-and-forget）与 `CallAsync`（请求-响应）API。
- InProc Transport：本地消息短路，无需序列化，可通过 `InProcTransportOptions` 切换为排队模式以模拟远程语义。
- TcpTransport + 静态注册表：长度前缀帧、握手、心跳与错误处理，支持基于静态配置的跨进程 send/call。
- 核心单元测试：覆盖顺序性、异常处理、唯一服务解析等关键场景。

## 声明接口并生成代理

在业务侧声明接口并标记 `[SkynetActor]` 属性，SourceGenerator 会在编译时生成代理、调度器与 MessagePack 序列化定义：

```csharp
[SkynetActor("login", Unique = true)]
public interface ILoginActor
{
Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
ValueTask NotifyAsync(LoginNotice notice);
string Ping(string name);
}

public sealed class LoginActor : RpcActor<ILoginActor>, ILoginActor
{
public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
{
return Task.FromResult(new LoginResponse(request.Username, $"Welcome {request.Username}!"));
}

public ValueTask NotifyAsync(LoginNotice notice)
{
// ...
return ValueTask.CompletedTask;
}

public string Ping(string name) => $"PONG: {name}";
}

[MessagePackObject]
public sealed record LoginRequest([property: Key(0)] string Username, [property: Key(1)] string Password);
```

运行时可以通过 `ActorSystem.GetService<ILoginActor>("login")` 获取强类型代理，调用时自动封送并保证 `Send` / `Call` 语义，同时默认使用 MessagePack 进行序列化。

## 跨进程示例

仓库提供一个最小的两节点 TCP 示例，演示如何通过静态注册表和 `TcpTransport` 建立跨进程调用：

```bash
# 终端 1：node1 监听 127.0.0.1:9101 并托管 echo actor
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --cluster node1

# 终端 2：node2 通过 TcpTransport 调用远程 echo actor
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --cluster node2
```

`node1` 输出 “Node1 listening on 127.0.0.1:9101” 后保持运行，`node2` 可以在命令行输入消息，通过 `CallAsync` 获取远程响应。示例使用长度前缀帧、握手与心跳保活，同时利用 `StaticClusterRegistry` 将服务名称映射到节点与已知 actor handle。

## 贡献指南

1. 阅读 [docs/PRD.md](docs/PRD.md) 了解产品需求与交付范围。
2. 在 [kanban](kanban/board.md) 中认领任务，确保状态同步。
3. 基于看板任务创建分支，提交 PR 时附带测试结果与影响说明。
4. 遵循 [CONTRIBUTING.md](CONTRIBUTING.md) 与仓库编码规范（见 [AGENTS.md](AGENTS.md)）。

欢迎通过 Issue、Discussion 或 PR 参与建设！
