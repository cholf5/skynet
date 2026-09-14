# Skynet 基准测试与基线报告

本工程为 PRD Milestone 7 验收项 "benchmark produced and baseline metrics logged" 提供可复现的
性能基线。基准 harness 是轻量自研控制台程序（`benchmarks/Skynet.Benchmarks`），**不引入
BenchmarkDotNet**：三个场景都是固定时长的闭环测量，几秒即可出结果，便于 CI 与回归对比；
BenchmarkDotNet 的微基准流程（多轮 iteration、内存诊断）对本项目的吞吐/延迟目标收益有限且拖慢 CI。

## 场景

| 场景 | 内容 | 测量 |
|------|------|------|
| `local-tell` | 单个 no-op actor 的 fire-and-forget（`SendAsync`）吞吐；带 mailbox 背压（队列 > 100k 时让路） | msg/s（按 metrics 处理计数差值统计，先排空再计量） |
| `local-call` | 本地 no-op actor 的 `CallAsync<string>` 往返（短路径，无序列化） | calls/s 与 p50/p90/p99 延迟 |
| `remote-call` | 同进程双节点，经 `TcpTransport`（loopback）跨节点 `CallAsync<string>`（含 MessagePack 序列化 + TCP 帧） | calls/s 与 p50/p90/p99 延迟 |

每个调用/发送都是真实闭环操作（无 mock），payload 为单字段消息 `Ping`，远端回复常量 `"ok"`。

## 运行方式

```bash
# 单场景
dotnet run --project benchmarks/Skynet.Benchmarks -c Release -- --scenario local-call --duration 5

# 全场景 + 结果归档（JSON + 文本），可传时长参数（默认 5s）
scripts/run-benchmarks.sh 5
```

参数：`--scenario <local-tell|local-call|remote-call>`、`--duration <seconds>`、
`--concurrency <n>`（并发生产者/调用者数量）、`--json <path>`（结构化结果）。
未知场景返回退出码 2。

## 基线数据

记录环境：

- 日期：2026-09-15
- 硬件：Apple M2，8 GB RAM，macOS 26.5（arm64）
- 运行时：.NET Runtime 10.0.1（仓库目标 net9.0；本机无 9.0 runtime，以 `DOTNET_ROLL_FORWARD=LatestMajor`
  运行，脚本内置该兜底。CI（GitHub Actions ubuntu-latest）安装 9.0.x，预期数据接近）
- 构建：Release

| 场景 | 并发 | 吞吐（ops/s） | p50 (ms) | p90 (ms) | p99 (ms) |
|------|------|--------------:|---------:|---------:|---------:|
| local-tell  | 1 | 1,657,988 | — | — | — |
| local-tell  | 4 | 1,287,890 | — | — | — |
| local-call  | 1 |   518,868 | 0.158 | 0.246 | 0.625 |
| local-call  | 4 |   629,552 | 0.538 | 0.938 | 1.971 |
| remote-call | 1 |    21,910 | 4.279 | 6.442 | 10.188 |

原始输出与 JSON 见 `benchmarks/results/`（文件名含时间戳）。

### 解读

- 单 actor 消息处理吞吐（local-tell ~166 万 msg/s）满足 AGENTS.md 的 ≥ 10 万条/s 目标一个数量级以上。
- 本地 call RTT p50 0.16 ms，满足 PRD "本地 call 延迟尽可能低" 的预期（目标 < 1 ms）。
- 并发从 1 提升到 4 时 local-call 吞吐 +21%，说明单 actor mailbox 是吞吐瓶颈，符合 actor 串行语义；
  local-tell 吞吐下降是生产者背压与单消费者结构所致。
- remote-call 的主要成本是 TCP loopback RTT + MessagePack 序列化，p99 约 10 ms，可用于容量规划参考。
- 偶发 max 尾延迟（百毫秒级）来自 GC 停顿，属正常现象；评估时请以 p99 为准。

## 复现与回归

- 复现：`scripts/run-benchmarks.sh 5`；对比两次运行时请使用相同时长、并关闭高负载后台进程。
- 更新基线：硬件/框架大版本变化后重跑脚本，并更新本文件表格与记录环境。
- 结果文件不要当作 CI 断言输入——基线数字因机器而异，仅用于人工回归对比。
