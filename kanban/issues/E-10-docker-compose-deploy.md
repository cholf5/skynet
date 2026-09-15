# E-10 Docker compose 与部署脚本完善

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：S
- 依赖：无

## 目标
补齐 PRD 5.4 可部署性尾巴：现仅根目录 Dockerfile（--gate 模式）。增加 docker-compose 样例
（双节点 cluster + gate 拓扑）、`.dockerignore` 校验、部署脚本（build/push 镜像）。

## 子任务
- [x] `docker-compose.yml`：node1/node2/gate 三容器组网演示（静态 registry 配置）。
- [x] `scripts/docker-build.sh`（可选 push 参数）。
- [x] CI/release 工作流增加可选镜像构建 job（独立 `workflow_dispatch` 工作流，未改动既有 build/test）。
- [x] 文档：docs/release-guide.md 增补 "Docker Compose 部署" 章节（含拓扑图、启动/验证步骤、FAQ）。

## 开发记录（2026-09-15）

### 容器化组网方案选择：环境变量覆盖（采用） vs host 网络（放弃）
- **host 网络模式被否决**：`network_mode: host` 在 Docker Desktop（macOS/Windows）上不可用或受限，端口无隔离，且本仓库开发者大量使用 macOS，可移植性差。
- **采用环境变量覆盖静态注册表 host**：`StaticClusterNodeConfiguration.Host` 默认 `127.0.0.1`，容器间互 discover 必须改为 compose 服务名。新增通用规则：对静态注册表中每条节点配置，环境变量 `SKYNET_NODE_{ID}_HOST`（节点 id 大写、`-`→`_`）覆盖其 host（`Program.ApplyHostOverrides`）。该规则对所有节点一致，无需区分"自己/对端"（比任务卡示例的 `SKYNET_NODE_HOST/PEER_HOST` 二变量方案更简洁，单规则即可覆盖三节点拓扑）。
- **gate 加入集群**：原 `--gate` 模式完全独立于集群。新增 `SKYNET_CLUSTER_NODE_ID` 环境变量：设置后 gate 以静态注册表第三个节点（host 服务名、端口 9103、HandleOffset 3000）的身份构建 `StaticClusterRegistry + TcpTransport`（`Program.CreateGateActorSystem`）。传输链路是首次发送时才建立的（lazy），因此 gate 启动后向 `echo` actor 发一次预热调用（`WarmUpClusterLinkAsync`），既在双方日志中立即可见集群连通，又保持心跳链路常开；预热失败（对端未就绪）仅告警、不影响 gate 服务。
- **gate 端口/绑定容器化**：`GateServerOptions` 默认 `TcpAddress=Loopback`、`WebSocketHost="localhost"`，在容器内不可达。新增 `SKYNET_GATE_TCP_PORT`（默认 4010）、`SKYNET_GATE_WS_PORT`（默认 4011）、`SKYNET_GATE_BIND_ALL`（置 1/true 时 TCP 绑定 `0.0.0.0`、HttpListener 用通配 host `+`，`PublicWebSocketHost` 保持 localhost 以便打印端点）。不设环境变量时行为与原版本完全一致（已实测）。
- **非交互 stdin 兼容**：原示例用 `Console.ReadLine()` 阻塞等待退出；`docker compose up -d` 下 stdin 为 EOF，`ReadLine` 立即返回 null 导致进程启动即退出（compose 拓扑不可用的硬阻塞）。新增 `WaitForStopAsync`：`Console.IsInputRedirected` 时无限等待，靠 `docker compose stop` 的 SIGTERM 结束；交互场景行为不变。node2 在非交互模式下改为每 10s 向 node1 发一次 `probe` 远程调用，让"两节点互 discover"可在 `docker compose logs` 中直接验证。

### BUG-1（pre-existing，阻塞验收，已在 Examples 层修复）
- **现象**：node2 → node1 跨节点 `EchoRequest` 调用失败，报 `Failed to serialize ... EchoRequest value`，随后报 `Payload contract id ... is not registered on this node`。交互模式（`--cluster node2` 手工输入）同样失败——**这是 E-10 冒烟测试暴露的既有缺陷，并非本次改动引入**。
- **根因**（两层）：
  1. MessagePack 3.x 的 `StandardResolver` 不再为无标注类型动态生成 formatter（`FormatterNotRegisteredException`，已在 /tmp 一次性 scratch 项目复现验证）；示例的 `EchoRequest/EchoNotice` 是无标注 private record。修复：改为 `internal` + `[MessagePackObject(AllowPrivate = true)]` + `[property: Key(0)]`（与同文件 `LoginRequest` 的标注风格一致；`internal` 是 MsgPack012 的要求）。
  2. `PayloadContractRegistry` 的接收端从不"发明"类型：发送侧首次发送时自注册 contract id，但接收侧若未注册该 id 则丢弃帧。修复：`RunClusterSampleAsync` 启动时对两个 payload 类型调用 `PayloadContractRegistry.Register<T>()`（contract id 为 FullName 的确定性 FNV-1a 哈希，两侧天然一致）。
- 两处修复均只在 `src/Skynet.Examples/Program.cs`，未触碰 Skynet.Core/Cluster。

### 静态校验结果（本机无 docker，无法运行 `docker compose config`）
用 python3 + PyYAML 对 `docker-compose.yml` 与 `.github/workflows/docker-image.yml` 做了校验，全部通过：
- YAML 语法解析通过；merge key（`x-examples-image` 锚点）正确展开到三个服务。
- 拓扑断言：三服务齐全；node1/node2 的 entrypoint 整体覆盖为 `--cluster nodeX`（Dockerfile ENTRYPOINT 固定 `--gate`，故必须整体覆盖而非追加 command）；gate 无 entrypoint/command 覆盖，沿用镜像默认 `--gate`；gate `depends_on` node1/node2 `service_healthy`；网络 `skynet-demo-net`（bridge）；gate 发布 2112/8080，node 集群端口仅发布到 `127.0.0.1`。
- 环境变量交叉核对：compose 中的 `SKYNET_NODE_*_HOST` 与 Program.cs 的命名规则逐一匹配，取值均为存在的服务名；gate 专属变量仅出现在 gate 服务；所有 env 值为字符串类型（曾发现 `2112` 被 YAML 解析为 int，已加引号修正）。
- healthcheck 命令以 `sh -n`/`bash -n` 校验语法（含 gate 8080 探测的内层 bash -c 与 CRLF 转义）；runtime 镜像无 curl，故用 bash 内建 `/dev/tcp` 探活。
- `scripts/docker-build.sh` 通过 `bash -n`。
- `.dockerignore`（本次新建，原先不存在）：排除 `.git/`、`.github/`、`**/bin|obj`、`artifacts/`、`kanban/`、`scripts/` 等；**刻意保留** `tests/`、`benchmarks/`（`dotnet restore Skynet.sln` 需要其 csproj）与 `docs/`（Dockerfile 有 `COPY docs ./docs`）。

### 本地功能性冒烟（无 docker 环境下的替代验证）
compose 无法实机运行，但组网逻辑（环境变量覆盖 + 静态注册表 + gate 入集群）可在宿主机以真实进程验证，已全部通过：
- node1/node2 以非交互 stdin（`< /dev/null`）+ host 覆盖启动：日志输出 override 行，node1 不再因 stdin EOF 退出；node2 每 10s 输出 `[remote] probe`，node1 同步输出 `[call] probe` —— 跨节点 MessagePack 往返成功。
- gate 以 `SKYNET_CLUSTER_NODE_ID=gate` + `BIND_ALL` 启动：监听 `0.0.0.0:2112`；`curl http://127.0.0.1:8080/ws/` 返回 **400**（与 compose healthcheck 的断言一致）；`nc -z 127.0.0.1 2112` 连通；`lsof` 确认 gate 与 node1 之间存在 ESTABLISHED 心跳链路；gate/node1 日志分别出现 warm-up 回包与 `[call] gate-handshake`。
- compose healthcheck 的两条命令在宿主机按原样执行均 PASS，且 connect-close 探测后 gate 进程存活、无异常日志。
- 不设任何环境变量的 `--gate`：仍为 `127.0.0.1:4010 / ws://localhost:4011`，向后兼容。

### docker 环境缺失限制（如实记录）
- 本机未安装 docker，`docker build` / `docker compose up` / `docker compose config` 均未执行；compose 文件仅做静态校验（见上）。首条 compose 验收（`docker compose up` 后两节点互 discover、gate 可连接）待有 docker 的环境实测，镜像内 runtime（Debian + bash）与镜像层是否齐全也需实机确认。
- 新增的 `.github/workflows/docker-image.yml`（仅 `workflow_dispatch`，构建 + inspect + 导出 artifact，不推送、不改既有 CI）同样未实机触发过。
- 回归测试：`dotnet build Skynet.sln -c Release` 0 warning/0 error；`dotnet test Skynet.sln` 全绿（5 个测试项目，共 250 通过 / 0 失败）。注：任务卡基线写的是 272，本分支实测为 250（与 main 仅差一个 kanban docs 提交，差异非本任务引入，基线数字来源待核）。

### QA 备注建议（P2）
- P2：待 docker 环境实测 `docker compose up --build -d` 三容器拓扑 + healthcheck + 文档 5.3 的两条验证命令；同时关注 runtime 镜像内 bash/grep 可用性（静态判断可用的依据是 Debian 基础镜像自带 coreutils/bash）。

## 验收标准
- `docker compose up` 后两节点互 discover，gate 可连接并转发到集群内 actor。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | compose up 三容器 | 日志显示节点互注册 |
| 2 | 客户端连 gate 发请求 | 收到集群 actor 响应 |

## 相关文件
- `Dockerfile`
- `scripts/publish-packages.sh`
- `docs/release-guide.md`
