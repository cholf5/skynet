# D-4 Actor 重入语义决策（await CallAsync 挂起 mailbox 循环）

- **来源**：D 系列 Code Review 确认"未处理"；上一轮架构评审已标记为核心语义悬而未决分支
- **开发者**：AI Agent
- **估算复杂度**：M（决策 + 实现 + 测试）
- **依赖任务**：无；建议最先做，因为它决定后续所有 Call 图文档与示例的写法

## 目标

对"handler 内 `await CallAsync` 挂起整个 mailbox 循环"这一核心语义做出架构决策（ADR）并落地。当前 `ActorHost.RunAsync` 逐条 `await ProcessMessageAsync(message)`（`ActorHost.cs:95`），A call B、B 回调 A 即死锁。云风原版 skynet 的 `skynet.call` 是协程挂起（actor 执行线程 await 期间可继续处理其他消息），本实现是 Actor-on-async 模型的经典陷阱，**这不是成熟度问题，是核心语义必须现在定死的分支**——生态越大，改动的破坏面越大。

## 候选方案（ADR 需逐一评估）

1. **方案 A：文档化 DAG 约束**。维持现状，明确"call 图不得成环"，提供死锁检测/诊断工具（如 call 链 trace + 超时告警）。成本最低，但把陷阱留给用户。
2. **方案 B：重入 mailbox**。handler await 期间循环继续读下一条消息（消息级重入）。解除死锁，但破坏"严格顺序处理"语义——同一 actor 的两条消息可能交错执行，需明确哪些语义受影响（AGENTS.md 测试清单里的"消息顺序性"）。
3. **方案 C：skynet 式逻辑协程**。为 call 引入 continuation 挂起/恢复机制（await 不阻塞 mailbox，当前消息的处理挂起，其余消息照常，被 call 的消息恢复后继续）。语义最接近原版 skynet，实现成本最高。

## 子任务

1. 写 ADR（`docs/adr/` 或 `docs/`）：三个方案的语义矩阵（顺序性保证 / 死锁面 / 实现复杂度 / 与 skynet 原版对齐度）、推荐结论。
2. 按结论实现（若选 A，实现的是死锁诊断：call 链追踪 + 环检测或至少文档 + FAQ；若选 B/C，改造 `ActorHost.RunAsync` 状态机）。
3. A→B→A 环状调用回归测试：方案 A 下断言明确的超时/诊断错误，方案 B/C 下断言环完成。
4. 更新 `docs/architecture.md` 与 AGENTS.md 测试清单中的语义描述。

## 验收标准

- ADR 合并且结论明确、有依据（不是"待定"）。
- 存在自动化测试固化所选语义（环状调用的行为是断言出来的，不是没测过）。
- 文档与实现一致；`Actor_Should_Process_Message_In_Order` 类既有语义测试的预期随决策调整并说明理由。

## 测试用例

1. 环状调用：A call B，B call A → 按所选方案断言（超时+诊断 / 完成）。
2. 顺序性：方案 B/C 下重新定义并验证"哪些消息间顺序仍保证"。
3. 性能基线：方案 B/C 不得使本地 no-op call 延迟显著退化（对照 PRD 基线流程补测）。

## 相关文件

- `src/Skynet.Core/ActorHost.cs`、`ActorSystem.cs`（CallAsync 路径）
- 参考：原版 skynet 的 `skynet.call`/协程语义；Phonest 的 IOManager 投递模型（反面参照）
