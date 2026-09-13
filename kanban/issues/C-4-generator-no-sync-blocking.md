# C-4 生成代码禁用同步阻塞

## Goal
消除生成代码中的 `.GetAwaiter().GetResult()` 同步阻塞：fire-and-forget 不应等待入队完成，同步返回类型直接禁止——skynet 的协程阻塞语义在 C# 里必须表达为 async，翻译成线程阻塞就是死锁陷阱（actor 消息处理内调 proxy 即死锁）。

## Subtasks
- [ ] `SkynetActorGenerator.cs:430`：`void` 方法生成的 proxy 改为真正的 fire-and-forget（丢弃入队 Task 或提供可选 await 形态），移除 `GetResult()`。
- [ ] `SkynetActorGenerator.cs:453`：接口约束只允许 `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` 返回类型，同步返回类型报诊断错误（新增 SKY0xx）。
- [ ] 审查 `Skynet.Cluster/RedisClient.cs:100,130` 的订阅/退订路径同步阻塞，评估是否可消除或加注释说明约束。
- [ ] 更新现有合约接口与示例代码适配新约束。
- [ ] 文档说明"同步返回类型不再支持"的 breaking change 与迁移方式。

## Developer
- Owner: AI Agent
- Complexity: S

## Acceptance Criteria
- 生成代码中 grep 不到 `GetAwaiter().GetResult()`。
- 含同步返回方法的合约接口编译报 SKY0xx 诊断。
- 现有示例/测试在新约束下编译运行通过。

## Test Cases
- [ ] 生成器快照测试更新（快照中无同步阻塞形态）。
- [ ] `void` fire-and-forget proxy 行为测试（入队即返回，不等处理完成）。
- [ ] 同步返回类型诊断测试。

## Related Files / Design Docs
- `src/Skynet.Generators/SkynetActorGenerator.cs`
- `docs/skynet-linus-code-review.md`（一般问题 #3）

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。skynet 的 `skynet.call` 是协程阻塞，C# 等价物是 await 而非线程阻塞。
