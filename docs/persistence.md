# Actor 持久化钩子（快照 / WAL 插件点）

对应 PRD 4.9 的可选项："Persistence hooks (optional): snapshot/wal API for user plugins (not enabled by default)"。持久化在 Skynet 中是一个**纯插件**：框架只定义接口与两个显式装配点，不绑定任何存储后端，也**从不**在自身生命周期里隐式做 I/O。

## 设计原则

1. **默认零开销**。不注册 store 时，持久化对运行时完全不存在：消息路径上没有任何判断、分配或 hook 调用；所有 actor 的行为与未引入本模块之前逐字节一致（既有全量测试即为回归证明）。
2. **双重 opt-in**。快照只在同时满足两个条件时才可能发生：
   - 系统侧：`ActorSystem.UseSnapshotStore(...)` 显式注册了 `IActorSnapshotStore`；
   - Actor 侧：actor 实现了 `ISnapshotable`。
   缺任一条件，`SnapshotActorAsync` / `RestoreActorSnapshotAsync` 快速失败（`InvalidOperationException`），不会触碰存储。
3. **持久化身份与运行时身份解耦**。`ActorHandle` 会在重启后被回收复用，不能作为持久化键；持久化键是调用方提供的稳定 `actorKey`（如 `"game:scoreboard"`、`"player:123"`）。
4. **payload 对框架不透明**。快照字节由 actor 自己生成与解释（推荐用带 `[MessagePackObject]` 属性的记录走 MessagePack，禁反射序列化）；框架与 store 只搬运不解析。

## 核心 API（Skynet.Core）

```csharp
namespace Skynet.Core.Persistence;

// 系统侧插件点：Save/Load 快照（异步，按 actorKey + 可选世代号）
public interface IActorSnapshotStore
{
    Task SaveAsync(ActorSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<ActorSnapshot?> LoadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default);
}

// Actor 侧 opt-in：只有实现该接口的 actor 才参与持久化
public interface ISnapshotable
{
    Task<byte[]> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
    Task RestoreSnapshotAsync(byte[] payload, CancellationToken cancellationToken = default);
}

// 键：稳定 actorKey + 世代号。世代 null 表示“取最新”；0 是不区分世代的覆盖槽
public readonly record struct ActorSnapshotKey(string ActorKey, long? Generation);

// 存储的一条快照：payload 是 actor 自治的不透明字节
[MessagePackObject]
public sealed record ActorSnapshot(string ActorKey, long Generation, DateTimeOffset CreatedAt, byte[] Payload);
```

### 装配与调用（ActorSystem 上仅此三处）

```csharp
var system = new ActorSystem();
system.UseSnapshotStore(new FileSnapshotStore("./data/snapshots")); // 唯一的注册点，可替换

var actor = await system.CreateActorAsync(() => new ScoreboardActor(), "scoreboard");

// 显式快照：capture + 存储在 actor 的 mailbox 循环内串行执行，返回已落盘的快照
var snapshot = await system.SnapshotActorAsync(actor.Handle, "game:scoreboard", generation: 1);

// 显式恢复：建议在创建实例后立刻由调用方调用
var applied = await system.RestoreActorSnapshotAsync(actor.Handle, "game:scoreboard"); // 无快照返回 null
```

### 执行模型（与 mailbox 的关系）

- `SnapshotActorAsync` / `RestoreActorSnapshotAsync` 的 hook 以**系统回调**形态进入目标 actor 的 mailbox（与定时器回调同路径）：与该 actor 的所有消息**严格串行**，绝不从外部线程读改 actor 状态。
- 快照：capture 在 actor 循环内执行，随后等 store 落盘完成才让出循环 —— `SnapshotActorAsync` 返回即代表"一个完整、持久的检查点，且此后消息都晚于检查点"。
- 恢复：store 读取在循环外完成（不占住 mailbox 做 I/O），payload 进入 mailbox 后在循环内应用 —— 调用方在 `RestoreActorSnapshotAsync` 返回之后发送的消息，必然看到恢复后的状态。
- 失败语义：hook 抛出的异常（含 store I/O 失败）原样返回给调用方；它**不会**杀死 actor，也不走 `HandleErrorAsync`（这是调用方驱动的操作，不是消息处理）。
- 若 actor 在 hook 排队后停止（被 `KillAsync`），操作以 `InvalidOperationException`/`ChannelClosedException` 明确失败，不会挂死调用方。

## 恢复时机：为什么是"显式恢复"而不是框架隐式做

本设计在框架生命周期里**不做任何隐式恢复**（不在 `CreateActorAsync` 里，也不在 `HandleStartAsync` 里）。理由：

1. **默认路径零开销/零副作用**：隐式恢复意味着框架必须"每次创建 actor 都先查一次存储"，未启用持久化的用户被无谓地拖入 I/O 语义；而显式恢复让没有持久化需求的系统连 store 都不需要存在。
2. **恢复失败策略属于业务**：快照缺失是"冷启动"还是"致命错误"？恢复校验失败要重试、降级还是拒启？这些选择只能由应用做出。框架替用户决定任何一种都会是错误的默认。
3. **恢复所需上下文框架没有**：哪个 actorKey 对应哪个新实例、要不要恢复多个 actor、恢复顺序如何——只有应用知道。
4. **测试与确定性**：显式调用让"保存 → 新建实例 → 恢复"成为普通代码，无需框架魔法即可在单测中复现完整重启流程。

代价是应用需要写一行恢复调用（见下方示例），换来的语义是"持久化是应用的决策，框架只是搬运工"。

## FileSnapshotStore（Skynet.Extras 样例实现）

单节点场景的文件系统样例 store：

- **目录布局**（actorKey 经 `Uri.EscapeDataString` 编码，非 ASCII/含分隔符的 key 安全且一一对应）：

  ```
  <root>/<url-encoded actorKey>/<世代，19位数字>.snapshot
  例如 ./data/snapshots/game%3Ascoreboard/0000000000000000001.snapshot
  ```

- **文件格式**：`"SKSNAP1"`（7 字节魔数）+ 4 字节小端 body 长度 + MessagePack 序列化的 `ActorSnapshot`（actorKey、世代、创建时间、payload）。帧头使**截断、尾部垃圾、格式错误**都能确定性地转为类型化的 `SnapshotCorruptedException`，绝不静默错读；"文件不存在"则返回 `null`（冷启动语义）。
- **写入原子性**：先写同目录唯一临时文件（`*.tmp`），flush 后 `File.Move(overwrite: true)` 原子替换。并发写同一 slot 时最后写者胜出，最终文件始终完整可读（POSIX rename / Windows MoveFileEx 语义）。
- 世代：`null` → 取目录内最大世代；正整数 → 精确文件；`0` → 不区分世代的覆盖槽。

## 使用示例（保存 → 新建实例 → 恢复）

```csharp
public sealed class ScoreboardActor : Actor, ISnapshotable
{
    private readonly Dictionary<string, long> _scores = new(StringComparer.Ordinal);

    [MessagePackObject]
    public sealed record ScoreboardState([property: Key(0)] Dictionary<string, long> Scores);

    protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Payload)
        {
            case Award a:
                _scores.TryGetValue(a.Player, out var total);
                _scores[a.Player] = total + a.Points;
                return Task.FromResult<object?>(_scores[a.Player]);
            case GetScore q:
                return Task.FromResult<object?>(_scores.GetValueOrDefault(q.Player));
            default:
                throw new InvalidOperationException("unknown payload");
        }
    }

    // 在 actor 循环内被框架回调：可安全访问状态
    public Task<byte[]> CaptureSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult(MessagePackSerializer.Serialize(new ScoreboardState(new(_scores))));

    public async Task RestoreSnapshotAsync(byte[] payload, CancellationToken ct = default)
    {
        var state = MessagePackSerializer.Deserialize<ScoreboardState>(payload);
        _scores.Clear();
        foreach (var (player, score) in state.Scores)
        {
            _scores[player] = score;
        }
        await Task.CompletedTask;
    }
}

// 重启恢复：旧 system 已 Dispose，新 system 挂同一个 store 目录
await using var system = new ActorSystem();
system.UseSnapshotStore(new FileSnapshotStore("./data/snapshots"));
var actor = await system.CreateActorAsync(() => new ScoreboardActor(), "scoreboard");
await system.RestoreActorSnapshotAsync(actor.Handle, "game:scoreboard"); // null = 冷启动
```

完整可运行版本见 `tests/Skynet.Extras.Tests/SnapshotEndToEndTests.cs`。

## WAL 状态

`Skynet.Core.Persistence.IActorWal`（`AppendAsync` / `ReplayAsync` + `WalRecord`）已作为**契约预留**：定义了每 actorKey 追加日志、按序号回放的语义（快照 + 从快照序号回放 = 完整恢复）。框架**没有提供实现、也没有任何运行时接线**——何时把记录写入 WAL、如何与快照对齐序号，完全由应用决定。做样例实现（如分段文件 WAL）是后续独立任务。

## 限制与注意事项

- `FileSnapshotStore` 是**样例级**实现：无压缩、无加密、无跨节点锁、无用户 payload 的 schema 迁移；适用于单节点或"每节点独立持久化"的部署。
- `FlushAsync` 刷到 OS 缓冲，不强制 fsync；对崩溃窗口有极致要求的应用应在自己的 store 实现里用 `Flush(flushToDisk: true)` 或 fsync 语义的存储。
- 编码后的 actorKey 超过 200 字符会被拒绝（文件名预算）；actorKey 必须非空。
- payload 的兼容性是 actor 的责任：改变状态结构时需要处理旧快照的反序列化（建议在 payload 里自带版本字段）。
- Windows 上替换一个正被读取的快照文件可能因共享冲突短暂失败；Linux/macOS 无此问题。
- 快照/恢复会占用 actor 的消息循环一小段时间（capture + 落盘期间消息排队）；对高频消息 actor 建议低频快照，并配合 WAL（预留）减小窗口。
