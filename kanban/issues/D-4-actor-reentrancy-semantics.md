# D-4 Actor 重入语义决策（await CallAsync 挂起 mailbox 循环）

- **来源**：D 系列 Code Review 确认"未处理"；上一轮架构评审已标记为核心语义悬而未决分支
- **开发者**：AI Agent
- **估算复杂度**：S（已决策，收窄实现）
- **依赖任务**：无

## 决策（维护者已确认）

采用 **方案 A+：DAG 文档化 + 环检测诊断**。

- 默认语义维持现状：mailbox 严格串行，handler 内 `await CallAsync` 挂起循环（消息 N 完成后才处理 N+1），AGENTS.md 顺序性承诺不变。
- 框架责任从"容忍环"改为"立刻、清楚地指出环"：`CallAsync` 维护 AsyncLocal 调用链，目标 handle 已在链中即快速失败，报错打印完整环路径。
- 方案 C（skynet 式协程）被否决，理由记录进 ADR：C# 的 await 点远多于 skynet 的显式让出点，可实现的只有"所有 await 点交错"，牺牲状态隔离这一框架核心卖点，且无编译期防护。
- 方案 B（`[Reentrant]` opt-in）记入 backlog 预留（本卡不实现），参照 Orleans 先例。

## 子任务

1. 写 ADR（`docs/adr/0001-actor-reentrancy-semantics.md` 或仓库文档惯例位置）：三方案语义矩阵结论、A+ 胜出与 C 否决理由、B 的预留说明。
2. 实现调用链环检测：`ActorSystem.CallAsync`（及 proxy 生成的调用路径，确认共享入口）维护 AsyncLocal 调用链（每项含目标 handle + 消息 id）；检测到目标已在链中 → 抛出带完整环路径的专用异常（如 `ActorCallCycleException("a.login → a.db → a.login")`）。
3. 回归测试：A→B→A 环状调用断言快速失败且异常信息含环路径；非环深链（A→B→C→A' 不同 actor 实例）不断误报；并行/嵌套场景无 AsyncLocal 串扰。
4. 更新 `docs/architecture.md` 语义章节与任务卡。

## 验收标准

- ADR 合并，结论明确。
- 环状调用在开发期快速失败（不是悬挂），异常信息可直接定位环。
- 既有全量测试绿（零语义迁移）。

## 测试用例

1. 环：A 的 handler call B，B 的 handler call A → `ActorCallCycleException`，消息含环路径文本。
2. 非环深链：A→B→C，C 不回调 → 正常完成。
3. 并发隔离：两个独立调用链互不串扰（AsyncLocal 边界）。

## 相关文件

- `src/Skynet.Core/ActorSystem.cs`（CallAsync）、`ActorRef.cs`（确认 proxy 调用路径共享入口）
- `docs/architecture.md`
- 参考：原版 skynet 的 `skynet.call`/协程语义（ADR 对照用）

## Review 记录

- **规格审查**：通过，但指出 ADR 保证表述过强（"进程内 wait-for 环必然被检测"不成立），
  已补充披露：本机制覆盖"发起时因果栈环 100% 响亮失败"，不覆盖"收尾边为排队中 call 的
  wait-for 死锁"（三方互等、纯 call 并行环两个反例，覆盖方案列 backlog）与
  `_ = Task.Run(...)` 分离任务的误报方向（详见 ADR 0001"作用域与边界"）。
- **质量审查**：需修复后批准 → 已修复。修复项：(1) 环测试补上 b 的存活断言（验收点是
  两个 actor 均存活），环路径断言收紧为完整路径片段（避免 `"1"` 是 `"12"` 子串的弱断言）；
  (2) 环检查抽成 `ActorSystem.ThrowIfCyclicalCall` 私有方法，作为未来 [Reentrant] 的接缝；
  (3) `CreateEnvelope` 签名归位 Allman；删除 Send 测试冗余断言。
- **实现决策偏离说明**：卡片子任务 2 要求链项"含目标 handle + 消息 id"，实现只记 handle，
  消息 id 冗余是有意为之——同一调用链上重复出现同一 handle 即构成环，消息 id 不提供额外
  判定信息；环路径异常已含完整 handle（及注册名）路径，可直接定位。
- **结论**：修复后批准（QA Passed）。
