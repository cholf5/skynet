# Actor 定时器设施（skynet.timeout 对应物）

Skynet 的 `Actor` 基类内置定时器设施，对应 skynet 原版的 `skynet.timeout`：在 actor 内注册一次性 / 周期回调，到期后回调以系统消息形态投递回该 actor 的 mailbox，由 actor 的消息循环串行执行。

## API

`Actor` 派生类中可直接使用（`protected`）：

```csharp
// 一次性定时器：dueTime 后执行一次
TimerHandle handle = AddTimer(TimeSpan.FromSeconds(5), async token =>
{
    // 在本 actor 的消息循环内执行
    Logger.LogInformation("timeout!");
});

// 周期定时器：每隔 interval 执行一次（首次同样在 interval 后）
TimerHandle periodic = SchedulePeriodic(TimeSpan.FromMilliseconds(100), async token =>
{
    await PollDatabaseAsync(token);
});

// 取消定时器，返回是否确实取消了仍在等待的定时器
bool cancelled = CancelTimer(periodic);
```

语义要点：

- **注册线程安全**：`AddTimer` / `SchedulePeriodic` / `CancelTimer` 可从任意线程调用（包括 actor 自己的消息循环、其他 actor、线程池任务），内部由调度器的锁保护，没有 owner 线程限制。
- **串行执行**：定时回调与普通消息共享同一个 mailbox，绝不与该 actor 的其他消息并发执行，也不引入额外线程进入 actor 状态。
- **异常隔离**：回调抛出的异常走与普通消息一致的 `HandleErrorAsync`（OnError）路径，不会杀死 actor。
- **生命周期**：actor 被 `KillAsync` 停止或 `ActorSystem` 关闭时，其所有未触发定时器被自动清理，不再触发、不泄漏。
- **句柄**：`TimerHandle` 在单个 `ActorSystem` 内唯一；`CancelTimer` 只能取消本 actor 注册的定时器，已触发或已取消的定时器返回 `false`。

## 并发模型

- 每个 `ActorSystem` 拥有一个 **`ActorTimerScheduler`**：单个后台调度线程（`IsBackground = true`，不阻止进程退出）+ `PriorityQueue` 按到期时间排序 + 字典索引。
- 注册 / 取消通过锁保护的索引 **同步生效**（`CancelTimer` 能立刻返回结果）；堆中残留的过期条目在到期弹出时 **懒删除**，注册 / 取消均为 O(log n)，万级定时器无 O(n) 扫描。
- 调度线程到期后只做投递：把定时器消息写入目标 actor 的 mailbox，**从不在线程池或调度线程上执行用户回调**。

### 周期定时器的合并（coalescing）

周期定时器带有 pending 标记：上一次 tick 还在目标 actor 的 mailbox 中排队或正在执行时，调度器跳过本次投递、仅顺延下一次到期时间。这保证 actor 短暂卡顿时不会堆积大量重复回调（下游最多积压一个 tick），与 Sharpest `AsyncTimerManager` 不合并回调导致堆积的问题不同。

## 使用注意

- 定时间隔为 0 的一次性定时器会在下一次调度循环中尽快触发。
- `SchedulePeriodic` 的 `interval` 必须大于零；`AddTimer` 的 `dueTime` 不能为负。
- 周期回调执行时间建议显著小于 interval；否则多余的 tick 会被合并掉（表现为丢拍而非堆积）。
- 定时器消息同样计入 actor 指标（队列长度、处理耗时），可在 DebugConsole 的 `stat` 中观察。
