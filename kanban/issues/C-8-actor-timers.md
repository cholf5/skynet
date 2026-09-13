# C-8 Actor 定时器设施（skynet.timeout 对应物）

## Goal
Skynet 目前没有任何 actor 内定时器设施。参考 Sharpest `TimerManager` 的设计补一个：actor 内注册一次性/周期回调、取消、到期投递回该 actor 的 mailbox 串行执行（不引入额外线程语义），对应 skynet 原版 `skynet.timeout`。

## Subtasks
- [ ] 设计 API：`Actor` 基类提供 `AddTimer(TimeSpan, callback)` / `SchedulePeriodic` / `CancelTimer`，到期消息以系统消息形态入目标 actor mailbox。
- [ ] 实现调度器：PriorityQueue + 字典索引 + 懒删除（参考 `Sharpest.Core/TimerManager.cs:153-168` 回调锁外执行的做法）。
- [ ] 并发模型决策：单一全局调度线程投递到期消息（推荐，mailbox 天然串行化），并写进文档。
- [ ] actor Kill/Stop 时自动清理其定时器。
- [ ] 单元测试 + 示例。

## Developer
- Owner: AI Agent
- Complexity: S

## Acceptance Criteria
- 定时回调始终在目标 actor 的消息循环内串行执行（与普通消息不并发）。
- Kill actor 后其未触发定时器不再触发且不泄漏。
- 周期定时器可取消；大量定时器（1 万+）下调度开销可接受。

## Test Cases
- [ ] 到期顺序性与串行性（定时器回调与普通消息互斥）。
- [ ] 取消 / Kill 清理 / 周期定时器。
- [ ] 压力：1 万定时器注册/触发/取消。

## Related Files / Design Docs
- `E:\dev\cholf5\Sharpest\src\Sharpest.Core\TimerManager.cs`、`AsyncTimerManager.cs`
- `src/Skynet.Core/ActorHost.cs`（mailbox 注入点）
- `docs/PRD.md`（skynet 语义对齐）

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。注意 Sharpest 的 `AddTimer` 有 owner 线程检查而 `Schedule` 没有（API 不一致），以及 `AsyncTimerManager` 不合并重复回调导致主线程卡顿时堆积——这两点在新实现中规避。
