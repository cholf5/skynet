# E-3 Benchmark 工程 + 基线压测报告

## 状态
- 列：To Do
- Owner: AI Agent
- 复杂度：M
- 依赖：无（建议在 E-1/E-2 合并后运行，避免基线漂移）

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
（待填）

## Review 意见
（待填）

## QA 记录
（待填）
