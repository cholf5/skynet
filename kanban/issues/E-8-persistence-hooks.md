# E-8 Persistence hooks（snapshot/WAL 插件点 + 样例）

## 状态
- 列：In Review
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 4.9 可选项：提供 actor 状态持久化钩子 API（`IActorSnapshotStore`：Save/Load snapshot，
可选 WAL 追加日志），框架默认不启用；附带一个文件系统实现样例与有状态 actor 演示。

## 子任务
- [x] `Skynet.Core` 定义 `IActorSnapshotStore` / `ISnapshotable` 钩子接口。
- [x] `Skynet.Extras` 提供 `FileSnapshotStore` 样例实现 + 有状态 actor 重启恢复示例。
- [x] 测试：快照保存/加载 roundtrip、损坏快照的容错。
- [x] 文档说明"持久化是插件，不启用则零开销"（docs/persistence.md）。

## 验收标准
- 不注册 store 时运行时无任何持久化开销；注册后 actor 可按钩子保存/恢复。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | Save → Load roundtrip | 状态一致 |
| 2 | 快照文件损坏 | Load 返回明确错误而非崩溃 |
| 3 | 未注册 store 的 actor | 行为与开销无变化 |

## 相关文件
- `src/Skynet.Core/`（Actor 基类）
- `docs/`（新增 persistence.md）

## 开发记录

### 接口设计（Skynet.Core.Persistence，只放契约）
- `IActorSnapshotStore`：`SaveAsync(ActorSnapshot)` / `LoadAsync(ActorSnapshotKey) → ActorSnapshot?`。
  返回 `null` 表示"无快照"（冷启动语义）；存储了但读不出必须抛类型化异常（Extras 侧为
  `SnapshotCorruptedException`），两者绝不混淆。
- `ActorSnapshotKey(ActorKey, Generation?)`：持久化键是调用方提供的稳定 `actorKey`，**不**使用
  `ActorHandle`（handle 会被回收复用，不能跨重启标识状态）。`Generation == null` 表示"取最新"，
  `0` 是不区分世代的覆盖槽，正整数为显式世代。世代约定写在接口 XML 文档里。
- `ISnapshotable`：actor 侧 opt-in（`CaptureSnapshotAsync` / `RestoreSnapshotAsync`，payload 为
  actor 自治的不透明 `byte[]`）。未实现该接口的 actor 与持久化完全无关。
- `ActorSnapshot`：MessagePack 属性标注的记录（key/世代/时间/payload），供 store 持久化信封。
- `IActorWal`（加分项，已做契约）：`AppendAsync`（返回单调序号）/ `ReplayAsync(fromSequence)` +
  `WalRecord`。**只定义接口，无实现、无运行时接线**，理由见下。
- 内部标记 `SnapshotRequested` / `SnapshotRestoreRequested`：仿照 `TimerFired`，仅作 envelope
  诊断负载，hook 实际逻辑挂在 mailbox 系统回调上。

### 装配点（ActorSystem 上仅三处新增公共 API）
- `UseSnapshotStore(IActorSnapshotStore)`：唯一注册点（可替换）；不注册则系统内不存在持久化。
- `SnapshotActorAsync(handle, actorKey, generation?)`：显式快照。hook 以系统回调进入目标 actor
  mailbox（与定时器同路径），**在 actor 消息循环内串行执行** capture，随后等 store 落盘完成才
  继续处理后续消息——返回即代表"完整、持久的检查点"。
- `RestoreActorSnapshotAsync(handle, actorKey, generation?)`：显式恢复。store 读取在循环外完成
  （不占 mailbox 做 I/O），payload 在循环内应用；调用方在该方法返回后发送的消息必然看到恢复后
  的状态。返回 null 表示无快照。
- 失败语义：hook 异常（含 store I/O 失败）原样抛给调用方；不杀死 actor、不走 `HandleErrorAsync`
  （调用方驱动的操作 ≠ 消息处理）。
- ActorHost 仅新增一个 internal 只读属性 `Stopped`（无语义变化），用于"actor 先于 hook 停机"时
  避免调用方永久挂起（mailbox 停机时会丢弃未处理消息）。

### 恢复时机取舍（二选一论证）：显式恢复，框架不做隐式 I/O
选择了"创建实例后由用户显式调用恢复"，而不是在 `CreateActorAsync` / `HandleStartAsync` 里隐式
恢复。理由：
1. **默认零开销**：隐式恢复迫使框架"每次创建 actor 都查一次存储"，未启用持久化的用户也被拖入
   I/O 语义与失败模式；显式恢复让无持久化需求的系统连 store 都不需要存在。
2. **失败策略属于业务**：快照缺失是冷启动还是致命错误？校验失败重试、降级还是拒启？只能由应用
   决定，框架任何默认值都可能是错的。
3. **框架缺少恢复所需上下文**：哪个 actorKey 对应哪个实例、恢复顺序、多 actor 依赖，只有应用知道。
4. **可测性**：显式调用让"保存 → 新建实例 → 恢复"是普通代码，单测即可复现完整重启（见
   `SnapshotEndToEndTests`），无需框架魔法。
代价是应用多写一行恢复调用；收益是"持久化是应用的决策，框架只是搬运工"。

### FileSnapshotStore（Skynet.Extras）文件布局与写入协议
- 布局：`<root>/<url-encoded actorKey>/<世代:D19>.snapshot`；actorKey 经 `Uri.EscapeDataString`
  编码（`@`、`/`、非 ASCII 等全部转义），键与目录一一对应；19 位定长数字便于解析与"最新世代"
  扫描。编码后超过 200 字符拒绝（文件名预算）。
- 文件格式：`"SKSNAP1"`（7 字节魔数）+ 4 字节小端 body 长度 + MessagePack(`ActorSnapshot`)。
  帧头使截断、尾部垃圾、格式错误确定性转为 `SnapshotCorruptedException`；文件不存在返回 null。
- 写入：同目录唯一临时文件（`*.tmp`）→ `FlushAsync` → `File.Move(overwrite: true)` 原子替换；
  并发写同一 slot 最后写者胜出，文件始终完整可读；失败时尽力清理临时文件。
- 读文件时以 `FileShare.Read | FileShare.Delete` 打开，减小与并发替换的共享冲突（Windows）。

### WAL 做不做：做契约、不做实现
任务卡允许加分项"只做追加日志接口"。已提供 `IActorWal`（append/replay + 序号语义，快照可声明
"从序号 N 起回放"）。不提供实现与接线的理由：WAL 的触发时机、fsync 策略、与快照的序号对齐都
强依赖部署场景（每消息一条 vs 每状态变更一条、磁盘介质），做成玩具样例反而会误导用户当成
 durability 保证；后续按需单开任务（如分段文件 WAL 或 SQLite 后端）。

### 测试结果
- `dotnet build Skynet.sln -c Release`：0 error 0 warning。
- `dotnet test Skynet.sln`：全绿 272 passed / 0 failed（本任务新增 22：
  Core `ActorSystemSnapshotTests` 8 + Extras `FileSnapshotStoreTests` 11 + `SnapshotEndToEndTests` 3）。
- 覆盖：roundtrip（含非 ASCII key、1 MiB payload）、世代/latest/unversioned 槽、截断/乱字节/
  尾部垃圾 → 类型化异常、并发写原子性、未注册 store / 非 ISnapshotable actor 快速失败且
  store 零调用、跨 ActorSystem 重启恢复端到端、killed actor 不挂起。

### 已知限制（详见 docs/persistence.md）
- FileSnapshotStore 为单节点样例：无压缩/加密/跨节点锁/payload schema 迁移；`FlushAsync` 不强制
  fsync，极端 durability 需求应在自定义 store 内实现。
- Windows 上替换正被读取的快照文件可能短暂共享冲突（读端已用 FileShare.Delete 缓解）。
- 快照/恢复期间 actor 消息循环被占用（capture + 落盘排队后续消息）；高频 actor 建议低频快照，
  配合 WAL（预留）减小窗口。

## Review（待填）
- 审查者：
- 意见：

## QA（待填）
- 执行记录：
- BUG 分级：
- QA Passed：
