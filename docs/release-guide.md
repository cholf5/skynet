# Release & Deployment Guide

本指南汇总了 Skynet 的 NuGet 发布、CI 流程与容器部署要点，便于维护者在不同环境下重复利用同一套资产。

## 1. 打包策略

- 所有项目共享 `Directory.Build.props/targets`，统一版本号、作者信息、仓库链接、符号包与 Source Link 设置。
- 默认版本前缀为 `0.1.0`，可通过 MSBuild 属性 `VersionSuffix` 附加预发布标记（例如 `dotnet pack -p:VersionSuffix=rc1` → `0.1.0-rc1`）。
- `Skynet.Generators` 以 `analyzers/dotnet/cs` 形式打包，便于消费侧直接获取 Source Generator。
- `PackageReadmeFile` 指向 `docs/nuget/package-readme.md`，在所有包中展示统一的简介与链接。

## 2. 手动发布步骤

```bash
# 1. 准备输出目录
rm -rf artifacts/nuget

# 2. 打包（生成 nupkg + snupkg）
dotnet pack Skynet.sln --configuration Release --include-symbols --include-source

# 3. 可选：验证本地包
./scripts/verify-packages.sh

# 4. 发布到 NuGet（需要设置 NUGET_API_KEY）
NUGET_API_KEY=<your-key> ./scripts/publish-packages.sh
```

`scripts/verify-packages.sh` 会创建一个临时控制台应用，通过根目录的 `nuget.config` 优先解析 `./artifacts/nuget` 包源并执行一次 `dotnet run`，确保关键依赖可被引用与运行。

## 3. GitHub Actions 发布

工作流文件：[`.github/workflows/release.yml`](../.github/workflows/release.yml)

- 触发条件：推送 `v*` 标签或手动触发 `workflow_dispatch`。
- 步骤：还原 → 打包 → 上传工件 →（可选）调用 `dotnet nuget push`。
- 可选输入：`versionSuffix`，用于在手动触发时临时追加预发布标记。
- 需要机密：`NUGET_API_KEY`（如果希望在流水线中自动推送到 NuGet.org）。

## 4. Docker 部署示例

- 根目录的 [`Dockerfile`](../Dockerfile) 基于多阶段构建，先在 SDK 镜像中执行 `dotnet publish`，然后拷贝到精简的 `mcr.microsoft.com/dotnet/runtime` 镜像。
- 默认入口为 `dotnet Skynet.Examples.dll --gate` 并暴露 8080 端口，可通过 `docker run skynet/examples -- --rooms` 覆盖示例模式。
- 若需要集成外部配置，可利用 `-v` 挂载 JSON/YAML 或 `-e` 注入环境变量，在容器启动脚本中读取。

## 5. Docker Compose 部署

仓库根目录的 [`docker-compose.yml`](../docker-compose.yml) 演示三容器拓扑：node1/node2 组成静态注册表集群，
gate 作为第三个节点加入集群并对外提供 TCP/WebSocket 接入。镜像由根目录 [`Dockerfile`](../Dockerfile)
构建（`skynet/examples:local`）。

> 注意：Skynet 仓库的开发环境当前没有安装 Docker，compose 文件与示例脚本仅经过静态校验
> （YAML 语法检查 + 字段人工核对），尚未实机运行过 `docker compose up`。首次使用时请先在
> 有 Docker 的环境中按下面的步骤验证一遍，遇到问题欢迎反馈到 issue。

### 5.1 拓扑

```text
              docker network: skynet-demo-net (bridge)
   ┌─────────────────────┐          ┌─────────────────────┐
   │ node1               │  心跳/互联 │ node2               │
   │ --cluster node1     │◄────────►│ --cluster node2     │
   │ 9101  集群传输       │          │ 9102  集群传输       │
   │ echo actor = #1001  │          │                     │
   └──────────┬──────────┘          └──────────┬──────────┘
              │            心跳（gate=9103）     │
              └───────────────┬────────────────┘
                       ┌──────┴───────┐
                       │ gate         │
                       │ --gate       │
                       │ 2112 TCP 接入 │
                       │ 8080 WS 接入  │
                       └──────┬───────┘
                              │ 发布到宿主机
                     2112/tcp, 8080/tcp
              （9101/9102 仅发布到宿主机回环，供排障）
```

容器间互 discover 的关键：静态注册表默认 host 是 `127.0.0.1`，容器内不可达。示例程序支持用
`SKYNET_NODE_{ID}_HOST` 环境变量逐节点覆盖（`{ID}` 为节点 id 大写，如 `SKYNET_NODE_NODE1_HOST=node1`），
compose 中统一改写为 compose 服务名。gate 容器额外设置 `SKYNET_CLUSTER_NODE_ID=gate`，以静态
注册表第三个节点（端口 9103）的身份加入集群，与 node1/node2 保持心跳。

| 环境变量 | 作用 | compose 取值 |
| --- | --- | --- |
| `SKYNET_NODE_{ID}_HOST` | 覆盖静态注册表中节点 `{ID}` 的 host | compose 服务名（`node1`/`node2`/`gate`） |
| `SKYNET_GATE_TCP_PORT` | `--gate` 模式 TCP 监听端口（默认 4010） | `2112` |
| `SKYNET_GATE_WS_PORT` | `--gate` 模式 WebSocket 监听端口（默认 4011） | `8080` |
| `SKYNET_GATE_BIND_ALL` | 置 `1`/`true` 时 TCP 绑定 `0.0.0.0`、WebSocket 以通配 host `+` 监听（容器内必需，否则端口只在容器回环可达） | `true` |
| `SKYNET_CLUSTER_NODE_ID` | 设置后 gate 以该节点名加入静态集群并维持心跳 | `gate` |

### 5.2 构建与启动

```bash
# 构建镜像（compose up --build 也会自动构建）
./scripts/docker-build.sh                                  # 默认 tag: skynet/examples:local
./scripts/docker-build.sh --tag registry.example.com/skynet/examples:0.1.0

# 启动三容器拓扑（gate 通过 depends_on 等待 node1/node2 健康后启动）
docker compose -f docker-compose.yml up --build -d

docker compose ps          # 三个容器最终均为 healthy
```

`scripts/docker-build.sh --push` 目前是占位实现：仅打印提示、不做实际推送。推送前请自行加上
registry 前缀并执行 `docker push`；接入凭据与多架构 buildx 留待后续任务处理。

### 5.3 验证

```bash
# 跨节点调用：node2 每 10 秒向 node1 的 echo actor 发起一次远程调用
docker compose logs node2 --tail 5        # 期望出现 "[remote] probe"
docker compose logs node1 --tail 5        # 期望出现 "[call] probe" 与 "[call] gate-handshake"
docker compose logs gate | grep -E "joined|warm-up"
# 期望出现 "Gate joined the sample cluster as node 'gate'."
# 以及 "Cluster warm-up: echo actor replied 'gate-handshake'."（gate 启动时会向 echo actor
# 发一次预热调用，以尽快建立并验证到集群的心跳链路）

# 验证 1：gate WebSocket HTTP 监听（非 WebSocket 请求按协议返回 400）
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8080/ws/    # 期望输出 400

# 验证 2：gate TCP 接入端口（telnet/nc 连接成功即表示监听可用）
nc -zv 127.0.0.1 2112        # 或 telnet 127.0.0.1 2112
nc -zv 127.0.0.1 9101        # 可选：已发布到宿主机回环的 node1 集群端口
```

客户端协议细节（帧格式、WebSocket 路径 `/ws/`、房间命令）参见 [`docs/rooms.md`](rooms.md)
与 [`docs/msg-server.md`](msg-server.md)。

### 5.4 常见问题

| 问题 | 解决方案 |
| --- | --- |
| 宿主机端口占用（2112/8080/9101/9102） | 只修改 `ports` 左侧的宿主机端口即可；容器内端口与 `SKYNET_*` 环境变量保持不变（集群互连走容器网络，与宿主端口无关）。 |
| gate 启动报 `Unable to resolve host 'node1'` | gate 构造静态注册表时会立即解析各节点 host。请用 `docker compose up` 保证三个服务在同一自定义网络；手动 `docker run` 需显式加入 `--network skynet-demo-net` 并使用服务名。 |
| 健康检查在日志里产生少量警告 | runtime 镜像不含 curl，healthcheck 用 bash 内建 `/dev/tcp` 探测端口；集群传输会把这类"无协议连接"记录为 `Failed to process incoming connection` 警告，属预期噪音，可调大 `interval` 缓解。 |
| 为什么不用 `network_mode: host` | host 网络在 Docker Desktop（macOS/Windows）上不可用或受限，且无法隔离端口；因此本拓扑选择服务名 DNS + 环境变量覆盖 host 的方案。 |
| 容器停不下来 / 重启循环 | 示例程序检测到 stdin 非交互（`docker compose up -d`）时会一直运行等待 `docker compose stop` 的 SIGTERM，不会因 stdin EOF 立即退出；若仍重启请查看 `docker compose logs` 中静态注册表的解析报错。 |

## 6. 常见问题

| 问题 | 解决方案 |
| --- | --- |
| `dotnet pack` 找不到 `package-readme.md` | 确认仓库中存在 `docs/nuget/package-readme.md` 并未被移动。 |
| 发布流水线无法访问 NuGet | 检查 `NUGET_API_KEY` 是否在仓库或组织层面配置为机密，并确认 Actions 允许访问。 |
| 验证脚本在 CI 中失败 | 请确保在运行脚本前安装 .NET 10 SDK，并在 Linux 环境下执行（脚本使用 Bash 语法）。 |

更多背景信息可参考 [`docs/PRD.md`](PRD.md) 以及各模块的专属文档（如 [`docs/redis-registry.md`](redis-registry.md)、[`docs/rooms.md`](rooms.md) 等）。
