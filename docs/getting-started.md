# 快速上手指南

本指南帮助你在本地环境中克隆、构建与运行 Skynet，覆盖基础示例与常用调试工具。

## 环境要求
- .NET SDK 9.0 及以上版本
- Redis（可选，用于体验 Redis 注册中心）
- Docker（可选，用于容器化部署示例）

确认版本：
```bash
dotnet --version
redis-server --version
```

## 克隆与还原依赖
```bash
git clone https://github.com/<your-org>/Skynet.git
cd Skynet
dotnet restore
```

## 构建与测试
```bash
dotnet build
dotnet test
```
> 如果当前环境缺少 .NET SDK，可参考 `dotnet-install.sh` 通过脚本下载本地化 SDK。

## 运行示例
示例项目位于 `src/Skynet.Examples`，提供多种演示模式：

### Echo 模式
```bash
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --mode echo
```
* 输入任意消息，系统会以 Actor 调用的方式返回相同内容。*

### Gate & Room 模式
```bash
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --mode gate --rooms
```
* 演示通过 GateServer 连接多个客户端、创建房间并进行广播。*

### 集群示例
1. 启动静态注册中心示例节点：
   ```bash
   dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --mode cluster --node gateway
   ```
2. 在另一终端启动工作节点：
   ```bash
   dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --mode cluster --node worker
   ```
3. 观察日志以确认跨进程消息流动。

## 使用调试控制台
调试控制台位于 `src/Skynet.Extras`，提供运行时查询命令：
```bash
dotnet run --project src/Skynet.Examples/Skynet.Examples.csproj -- --mode debug-console
```
常用命令：
- `help`：查看可用命令
- `actors`：列出当前注册的 Actor
- `metrics`：输出消息处理统计

## 配置与扩展
- `Directory.Build.props/targets`：集中管理包信息与版本。
- `src/Skynet.Core/ActorSystemOptions.cs`：调整队列长度、度量选项。
- `src/Skynet.Net/GateServerOptions.cs`：配置连接超时、心跳间隔与路由策略。

## 发布与验证
详见 `docs/release-guide.md` 与 `scripts/verify-packages.sh`，其中包含 NuGet 打包、容器化发布与验证脚本的使用说明。

## 常见问题
| 问题 | 解决方案 |
|------|----------|
| `dotnet` 命令不存在 | 使用 `dotnet-install.sh` 安装本地 SDK 或参考官方安装指南 |
| Redis 无法连接 | 确保本地 Redis 已启动并在 `appsettings.json` 中配置正确连接字符串 |
| 调试控制台未显示指标 | 确认示例使用 `--mode debug-console` 并在配置中启用指标采集 |

## 后续阅读
- [项目概览](overview.md)
- [架构设计解读](architecture.md)
- [发布指南](release-guide.md)
- [Redis 注册中心说明](redis-registry.md)

