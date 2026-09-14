# E-3 Benchmark 工程 + 基线压测报告

## 状态
- 列：Done
- Owner: AI Agent
- 复杂度：M
- 依赖：无（在 E-1/E-2 合并后运行，基线数据已含两次提交后的代码）

## 目标
落实 PRD Milestone 7 验收（"benchmark produced and baseline metrics logged"）与 13.3 性能测试要求：
新建 `benchmarks/Skynet.Benchmarks` 控制台工程，覆盖三类基线场景，并将本机基线结果记入 `docs/benchmarks.md`。

## 子任务
- [ ] Benchmark 工程（net9.0，引用 Skynet.Core/Skynet.Cluster），参数：`--scenario`、`--duration`、`--concurrency`、`--json`。
- [ ] 场景 1 `local-tell`：no-op actor 的 fire-and-forget 吞吐（msg/s）。
- [ ] 场景 2 `local-call`：no-op actor 的 CallAsync 往返延迟（p50/p95/p99）与并发吞吐。
- [ ] 场景 3 `remote-call`：同进程双节点 TcpTransport（loopback）远程 CallAsync 延迟与吞吐。
- [ ] `scripts/run-benchmarks.sh`：Release 构建并依次运行，输出存档到 `benchmarks/results/`。
- [ ] `docs/benchmarks.md`：方法论、场景说明、可复现命令、基线数据表。

## 验收标准
- `scripts/run-benchmarks.sh` 一键可复现。
- `docs/benchmarks.md` 记录实际运行的基线数据（含硬件/.NET 版本/日期）。
- 不引入 BenchmarkDotNet（轻量自研 harness，理由记录在文档：CI 快、免依赖）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | `--scenario local-call --duration 2s` 正常输出分位数 | 无异常，结果合理（p50>0） |
| 2 | 未知 scenario | 报错并列出可用场景 |
| 3 | `--json` 输出合法 JSON | 可解析 |

## 相关文件
- `src/Skynet.Examples/Program.cs`（双节点组网参考）
- `docs/skynet-linus-code-review.md`（"无可复现性能基准数据"缺口来源）

## 开发记录
- 新增 `benchmarks/Skynet.Benchmarks`（已加入 Skynet.sln），三场景闭环测量：local-tell（吞吐，metrics
  处理计数差值 + mailbox 背压）、local-call（CallAsync 延迟分位数）、remote-call（同进程双节点 TcpTransport
  loopback 跨节点 CallAsync）。支持 `--duration/--concurrency/--json`。
- 开发中踩坑：`CreateActorAsync` 返回 `ActorRef`（取 `.Handle` 才是 handle）；`SendAsync` 返回 ValueTask
  需 `AsTask()`；测量窗口关闭时未完成调用会抛 TaskCanceledException（循环内捕获并丢弃样本）；
  跨节点 payload 需 `[MessagePackObject(AllowPrivate=true)]` 且类型可见性 ≥ internal（MsgPack012）。
- 基线（Apple M2 / 8GB / .NET 10.0.1 runtime rollforward 运行 net9.0 / Release / 2026-09-15）：
  local-tell 1.66M ops/s（并发 4 → 1.29M）；local-call 519k ops/s p50=0.158ms p99=0.625ms
  （并发 4 → 630k ops/s p50=0.538ms）；remote-call 21.9k ops/s p50=4.279ms p99=10.188ms。
- 原始结果归档在 `benchmarks/results/`，报告与解读在 `docs/benchmarks.md`。

## Review 意见
- 审查确认：闭环测量（无 mock）；local-tell 以 metrics 处理计数为准且发送前有界排空；延迟样本在窗口关闭
  时丢弃而非计入取消异常；分位数索引做了 clamp；9221/9222 端口写死为 loopback 基线用（冲突时会报错，
  属可接受限制，文档已注明基线仅用于人工对比）。
- 脚本 `scripts/run-benchmarks.sh` 含 `DOTNET_ROLL_FORWARD` 兜底，与 CI（安装 9.0.x）行为一致。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | `--scenario local-call --duration 2s` 正常输出分位数 | ✅ 冒烟通过（p50>0） |
| 2 | 未知 scenario 报错退出 | ✅ "Unknown scenario 'bogus'" + 退出码 2 |
| 3 | `--json` 输出合法 JSON | ✅ python3 json.tool 解析通过 |
| 4 | `scripts/run-benchmarks.sh 5` 一键三场景 + 归档 | ✅ 6 个结果文件落盘 |
| 5 | 解决方案 Release 构建（含新工程） | ✅ 0 error |

无 P0/P1 问题。Milestone 7 "benchmark produced and baseline metrics logged" 达成。**QA Passed**
