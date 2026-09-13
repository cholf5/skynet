# ADR 0001：Actor 可重入语义与调用环检测

- 状态：已接受（Accepted）
- 日期：2026-09-14
- 关联任务：D-4（Actor 调用环检测）
- 备注：这是本仓库第一篇 ADR。

## 问题陈述

Skynet 的 mailbox 是严格串行的：`ActorHost.RunAsync` 逐条 `await ProcessMessageAsync`，
handler 内 `await CallAsync` 会挂起整个 mailbox 循环，直到被调方返回。这一语义是刻意
的设计——它保证每个 actor 内部状态访问天然单线程，与 AGENTS.md 的"消息严格顺序处理"
承诺一致。

但它带来一个致命后果：**环状调用会死锁**。若 actor A 的 handler 内 `await CallAsync(B)`，
而 B 的 handler 又 `await CallAsync(A)`，则 A 的 mailbox 在等 B 返回，B 的 mailbox 在等
A 返回，双方永远互等，调用永久悬挂且没有任何报错。用户面对的是"卡死"，而不是可诊断
的故障。

## 方案对比

| 方案 | 思路 | 结论 |
|------|------|------|
| A：保持串行 + 静态约定 | 文档要求调用图无环，违规自担 | 保留为文档约定，但无保护 |
| A+：保持串行 + 运行时环检测 | 语义不变；调用发起时检测环，立即抛 `ActorCallCycleException` | **采纳** |
| B：`[Reentrant]` opt-in 可重入 | 标注的 actor 在 await 点让出 mailbox | 记入 backlog 预留，本卡不实现 |
| C：skynet 式协程（所有 await 点交错） | 仿 skynet，让出 mailbox 处理下一条消息 | **否决** |

方案 C 的否决理由：skynet（Lua）中让出点只有显式的 `skynet.call`/`skynet.send`，语言层
协程保证 actor 状态只在 call 点被交错。C# 的 `await` 点遍布用户代码（`Task.Delay`、IO、
任意第三方异步调用），无法做到"只让 call 点让出"——可实现的是"所有 await 点交错"，
即两条消息的执行片段任意穿插，actor 状态隔离被彻底破坏，静默的数据竞争比死锁更难排查。

方案 B（`[Reentrant]`）是未来对少数无状态/精心设计的 actor 的逃生门，与 A+ 正交，
记入 backlog，不影响本决策。

## 决策（方案 A+）

保持 mailbox 严格串行语义完全不变；框架在调用发起的瞬间检测环，形成环时抛出
`ActorCallCycleException`（含完整环路径，如 `Actor call cycle detected: 1 → 2 → 1`），
把"永久悬挂"变成"响亮报错"。

### 链的流动模型（本决策的核心机制）

调用链（`ActorCallChain`）表示"当前因果调用栈上所有被挂起（正在等待嵌套 call 返回）的
actor，按从最外层调用者到当前执行者排序"。它只有 call 边：`SendAsync` 是 fire-and-forget，
不产生阻塞边，不进链、不检测。

链靠两个载体流动：

1. **actor 内部（AsyncLocal 环境流）**：`ActorHost.ProcessMessageAsync` 在处理消息开始时
   建立链作用域——取 envelope 携带的链（调用方序列），追加自己（envelope.To 即"自己"），
   存入 `AsyncLocal`（`ActorCallContext`，模式与 `TraceContext` 一致），`using`/try-finally
   保证异常路径也弹链。handler 内任意 `await` 点（包括并发分支）读到的都是本消息的链。
2. **跨 actor 边界（envelope 传播）**：`ActorSystem.CallAsync` 发起调用时，把当前链写入
   `MessageEnvelope.CallChain`。本地投递时目标 handler 在目标 actor 的 host 循环里执行，
   已经是不同的 `AsyncLocal` 上下文——目标 actor 的链由它自己的 ProcessMessageAsync 从
   envelope 重建并追加自己。因此链精确刻画了"因果调用栈上的挂起（等待）关系"。

**检测点**：`ActorSystem.CallAsync` 是唯一拦截位置——直接调用、`ActorRef.CallAsync`、
以及 Source Generator 生成的 RPC proxy（走 `ActorRef.CallAsync`）全部汇聚于此。发起
`CallAsync(handle)` 时若 `handle` 已在当前链中，立即抛 `ActorCallCycleException`，消息
打印完整环路径（handle 有注册名时一并列出）。此时尚未投递 envelope，无副作用。

**作用域与边界**：

- **保证的准确表述**：本机制保证"**发起时**已能从因果调用栈识别的环 100% 响亮失败"；
  它**不覆盖**"收尾边是排队中（尚未出队）的 call"构成的 wait-for 死锁——那条 call 还没
  发起，initiation-time 检测原理上看不到。两个反例：
  1. *三方互等*：A 的 handler `await CallAsync(B)`（call 已在 B 的 mailbox 排队，B 正在
     处理另一条消息 M）；M 的 handler `await CallAsync(A)`。M 的链 = [B] 不含 A，放行。
     结果 A 等 B 的响应、B 被 M 占住、M 等 A 的响应，永久死锁且零报错，而静态 call 图
     A→B→A 已成环。
  2. *纯 call 并行环*：A 的 handler `await Task.WhenAll(CallAsync(B), CallAsync(C))`；
     B 调 C、C 调 B。若 C 先处理 A 直发的消息（链 [A,C]），其 call B 发起时链不含 B，
     放行后 B↔C 互等死锁（若 C 恰好先处理 B 的消息则会被检测到，行为取决于出队顺序）。
  要覆盖此类死锁需 wait-for 图分析（记录"谁在等谁"的全局挂起图并在投递/入队时查环）或
  响应超时兜底（如为 `CallAsync` 提供默认超时），二者均为**backlog**，不在本决策范围。
  用户侧的务实缓解：对可能互调的长链显式传 `timeout`。
- **误报方向边界**：handler 内 fire-and-forget 分离的异步任务（如 `_ = Task.Run(...)`）
  经 ExecutionContext 继承发起时的链；该任务中回调发起链上某个 actor 的 call 会被误判
  为环。此形态罕见且报错响亮（路径完整可定位），规避方式是分离任务不直接 call 发起链
  上的 actor。
- 每条并发调用链持有独立的 `AsyncLocal` 上下文，互不串扰；同一 actor handler 内
  `Task.WhenAll` 的多个并行分支共享同一链前缀，各分支追加各自目标，发起时互不误报。
- 链是**进程内**语义：`MessageEnvelope.CallChain` 不参与 wire 序列化（不改 wire 协议版本），
  远端节点收到消息后由自己的 ProcessMessageAsync 从空链建立自己的链。
  **跨节点环（节点 1 的 A call 节点 2 的 B，B 又 call 节点 1 的 A）不在本决策的检测范围内**，
  仍会悬挂；如需覆盖，需在 wire 协议中加入链字段（版本升级）或引入 wait-for 图死锁检测，
  作为后续独立决策。
- 已知留白：actor 的 `OnStartAsync` 钩子在 mailbox 循环启动前执行，链尚未建立，
  其中发起的环调用不会被检测到。

## 后果

- **用户义务**：进程内的 call 图必须无环。需要"回调"形态的协作时，用 `SendAsync`
  （fire-and-forget，不进链）或拆分消息时序，而不是环状 call。
- **违规表现**：环调用立即得到 `ActorCallCycleException`（继承
  `InvalidOperationException`），随消息正常失败路径回传给最外层调用方；参与环的 actor
  不会崩溃，继续处理后续消息。修复前行为是无限悬挂。
- **语义零迁移**：所有合法（无环）程序行为不变；`SendAsync` 完全不受影响。
- 性能开销：每次 call 一次链查找（链深即调用深度，通常个位数）；每条消息一次
  AsyncLocal 作用域建立，可忽略。
