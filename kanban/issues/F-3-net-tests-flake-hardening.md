# F-3 Skynet.Net.Tests 偶发失败排查与加固

## 状态
- 列：To Do
- Owner: AI Agent（PM 执行）
- 复杂度：S/M
- 依赖：无

## 背景
E-6 与 E-9 两个 agent 在并行全量测试时各自观察到一次 Skynet.Net.Tests 偶发失败
（单跑及后续复跑均 96/96 绿），失败用例名未被记录。怀疑对象为真实时间依赖的测试
（首要嫌疑：GateRateLimitingTests.TokenBucketLimiter_ShouldThrottleThroughGate 的
令牌桶时序——重负载下帧 C 与帧 B 的评估间隔可能超过补桶周期，令 C 意外通过）。

## 目标
1. 尽力复现：连续多轮单套件 + 并行全量 + CPU 压力下跑 Net.Tests，捕获失败用例名与断言信息。
2. 若复现：修复根因。若不能复现：对真实时间依赖的测试做加固（拉大时序裕量或改为确定性
   limiter），并在任务卡如实记录"根因未捕获，按疑似对象加固"。

## 验收标准
- Net.Tests 连续 15 轮运行（含与其它测试项目并行、含 CPU 压力场景）零失败。
- 加固不以削弱断言语义为代价（时序裕量拉大而非断言删减）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 压力复跑 15 轮 | 0 失败 |
| 2 | 全量测试 | 301+ 全绿 |

## 相关文件
- `tests/Skynet.Net.Tests/GateRateLimitingTests.cs`（疑似对象）
- `tests/Skynet.Net.Tests/`（其它真实时间依赖测试排查）

## 开发记录
（待填）

## Review 意见
（待填）

## QA 记录
（待填）
