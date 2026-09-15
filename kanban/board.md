# Kanban Board

## Backlog
- _None_

## To Do
- _None_

## In Progress
- _None_

## In Review
- _None_

## Testing / QA
- _None_

## Blocked
- _None_

## Done
- [x] [F-3 Skynet.Net.Tests 偶发失败排查与加固](issues/F-3-net-tests-flake-hardening.md) — Owner: AI Agent — Complexity: S/M
- [x] [F-2 ConnectAsync 端口拒绝路径资源泄漏修复](issues/F-2-connect-async-leak-fix.md) — Owner: AI Agent — Complexity: S
- [x] [F-1 ADR-0002 框架边界与生态引用策略（含 Extras 身份标注）](issues/F-1-adr-framework-boundary.md) — Owner: AI Agent — Complexity: S
- [x] [E-10 Docker compose 与部署脚本完善](issues/E-10-docker-compose-deploy.md) — Owner: AI Agent — Complexity: S
- [x] [E-9 Consistent hashing placement 策略](issues/E-9-consistent-hash-placement.md) — Owner: AI Agent — Complexity: S
- [x] [E-8 Persistence hooks（snapshot/WAL 插件点 + 样例）](issues/E-8-persistence-hooks.md) — Owner: AI Agent — Complexity: M
- [x] [E-6 传输层故障注入与 Chaos 测试](issues/E-6-transport-fault-injection.md) — Owner: AI Agent — Complexity: M
- [x] [E-7 日志级别热 reload](issues/E-7-logging-hot-reload.md) — Owner: AI Agent — Complexity: S
- [x] [E-5 MsgServer 业务路由层](issues/E-5-msg-server-routing.md) — Owner: AI Agent — Complexity: M
- [x] [E-4 跨节点 TLS（TcpTransport 可选 SslStream 加密）](issues/E-4-tcp-transport-tls.md) — Owner: AI Agent — Complexity: M
- [x] [E-11 升级到 .NET 10（net9.0 → net10.0）](issues/E-11-dotnet-10-upgrade.md) — Owner: AI Agent — Complexity: M
- [x] [E-3 Benchmark 工程 + 基线压测报告](issues/E-3-benchmark-baseline.md) — Owner: AI Agent — Complexity: M
- [x] [E-2 Prometheus 指标导出插件](issues/E-2-prometheus-metrics-exporter.md) — Owner: AI Agent — Complexity: M
- [x] [E-1 Gate 限流 hook（连接准入 + 入站帧限速）](issues/E-1-gate-rate-limiting.md) — Owner: AI Agent — Complexity: S
- [x] [D-5 Gate 加密：重放防护与方向密钥分离](issues/D-5-gate-crypto-replay-hardening.md) — Owner: AI Agent — Complexity: M
- [x] [D-8 KCP 传输拆分为独立工程 Skynet.Transport.Kcp](issues/D-8-split-kcp-transport-project.md) — Owner: AI Agent — Complexity: S
- [x] [D-7 P2 零散修复批（17 项）](issues/D-7-p2-batch-fixes.md) — Owner: AI Agent — Complexity: S
- [x] [D-6 contract id 边界加固](issues/D-6-contract-id-edge-hardening.md) — Owner: AI Agent — Complexity: S
- [x] [D-3 KcpTransport 加固对齐（默认死链检测 + 入站会话治理）](issues/D-3-kcp-transport-hardening.md) — Owner: AI Agent — Complexity: M
- [x] [D-4 Actor 重入语义决策（await CallAsync 挂起 mailbox 循环）](issues/D-4-actor-reentrancy-semantics.md) — Owner: AI Agent — Complexity: S
- [x] [D-2 TcpTransport 并发遗留三连（ABA 删除 / 连接失败 fail-fast / MessageId 撞车）](issues/D-2-tcp-transport-concurrency-leftovers.md) — Owner: AI Agent — Complexity: M
- [x] [D-1 Gate 客户端发送互斥与接收帧上限](issues/D-1-gate-client-send-mutex.md) — Owner: AI Agent — Complexity: S
- [x] [A-1 Repository Skeleton & Tooling Bootstrap](issues/A-1-repo-skeleton.md) — Owner: AI Agent — Complexity: M
- [x] [C-9 KCP/UDP 传输 + ReliableQueue 可靠层（远期）](issues/C-9-kcp-reliable-transport.md) — Owner: AI Agent — Complexity: L
- [x] [C-6 移植 Gate 加密握手（RSA token，现代化改造）](issues/C-6-gate-encryption-handshake.md) — Owner: AI Agent — Complexity: M
- [x] [C-5 移植 Gate 出站连接池](issues/C-5-outbound-gate-connection-pool.md) — Owner: AI Agent — Complexity: M
- [x] [C-4 生成代码禁用同步阻塞](issues/C-4-generator-no-sync-blocking.md) — Owner: AI Agent — Complexity: S
- [x] [C-2 Contract ID 映射表：消灭远端 Type.GetType 反射](issues/C-2-contract-id-mapping.md) — Owner: AI Agent — Complexity: M
- [x] [C-7 RedisClusterRegistry 断线对账与自愈](issues/C-7-redis-registry-reconciliation.md) — Owner: AI Agent — Complexity: M
- [x] [C-3 TcpTransport 健壮性三硬伤](issues/C-3-tcp-transport-hardening.md) — Owner: AI Agent — Complexity: M
- [x] [C-8 Actor 定时器设施](issues/C-8-actor-timers.md) — Owner: AI Agent — Complexity: S
- [x] [C-1 修复构建红与测试债](issues/C-1-fix-build-and-test-debt.md) — Owner: AI Agent — Complexity: S
- [x] [B-3 Documentation — Architecture Deep Dive](issues/B-3-documentation-architecture.md) — Owner: AI Agent — Complexity: M
- [x] [B-2 Documentation — Getting Started Guide](issues/B-2-documentation-getting-started.md) — Owner: AI Agent — Complexity: M
- [x] [B-1 Documentation — Project Overview](issues/B-1-documentation-overview.md) — Owner: AI Agent — Complexity: S
- [x] [A-10 Packaging & Deployment Assets](issues/A-10-packaging.md) — Owner: AI Agent — Complexity: M
- [x] [A-9 Redis Registry Plugin](issues/A-9-redis-registry.md) — Owner: AI Agent — Complexity: M
- [x] [A-8 DebugConsole & Observability Hooks](issues/A-8-debug-console.md) — Owner: AI Agent — Complexity: M
- [x] [A-7 RoomManager & Multicast Sample](issues/A-7-roommanager.md) — Owner: AI Agent — Complexity: M
- [x] [A-6 GateServer & Session Pipeline](issues/A-6-gate-session.md) — Owner: AI Agent — Complexity: L
- [x] [A-5 TCP Transport & Static Registry](issues/A-5-tcp-transport.md) — Owner: AI Agent — Complexity: L
- [x] [A-4 InProc Transport & Echo Sample](issues/A-4-inproc-transport.md) — Owner: AI Agent — Complexity: M
- [x] [A-3 SourceGenerator & MessagePack Integration](issues/A-3-source-generator.md) — Owner: AI Agent — Complexity: L
- [x] [A-2 Core Actor Runtime & InProc Transport](issues/A-2-core-actor-runtime.md) — Owner: AI Agent — Complexity: L
- [x] [A-0 Initialize Kanban Workflow](issues/A-0-initialize-kanban.md) — Owner: AI Agent — Complexity: S
