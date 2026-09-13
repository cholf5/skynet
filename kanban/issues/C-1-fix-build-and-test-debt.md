# C-1 修复构建红与测试债

## Goal
让解决方案恢复"restore→build→test 全绿"的基本盘，为后续所有任务提供可信回归基线。当前实测：`dotnet test` 在 restore 阶段即失败（MessagePack 3.1.4 审计出多个高危漏洞 NU1903，叠加 TreatWarningsAsErrors 挡死）；关闭审计后 Net.Tests 挂死、Extras 1 例失败。

## Subtasks
- [x] 升级 MessagePack 到已修复漏洞的版本，或添加有依据的 NuGetAudit 抑制（记录 advisory 编号与理由），恢复 restore 通过。
- [x] 修复 `tests/Skynet.Net.Tests/BasicTest.VeryBasicTest` 挂死：所有 I/O 等待加 `WaitAsync(timeout)` 超时保护，失败快速反馈。
- [x] 修复 `Skynet.Extras.Tests.RoomManagerTests.BroadcastAsyncDeliversPayloadToAllMembers`：将 `Delivered` 计数改名为 `Enqueued`（SendAsync 只保证入队），同步更新 `RoomManager.cs` 指标语义与文档。
- [x] 在 CI 中加测试挂死保护（`--blame-hang-timeout`）。

## Developer
- Owner: AI Agent
- Complexity: S

## Acceptance Criteria
- `dotnet test`（不关 NuGetAudit）在本机与 CI 全绿退出。
- Net.Tests、Extras.Tests 无失败、无挂起。
- 无高危（High/Critical）漏洞告警处于 error 级别。

## Test Cases
- [x] `dotnet test` 全解决方案跑通。
- [x] 人为制造断线场景验证测试在超时后失败而非挂起。（Net.Tests 全部 I/O 等待加 `WaitAsync(10s)`；CI 用 `--blame-hang-timeout 5m` 兜底，本机用 blame-hang 实测能在超时后定位挂死测试）

## Related Files / Design Docs
- `docs/skynet-linus-code-review.md`（测试不稳定与指标语义问题的原始记录）
- `Directory.Packages.props`
- `src/Skynet.Extras/RoomManager.cs:109-111`

## Dependencies
- 无（本任务是所有后续任务的先决条件）

## Notes & Updates
- 2026-09-13：任务创建。背景：MessagePack 3.1.4 被 NuGet 审计判 NU1902/NU1903（多个 GHSA 高危），`TreatWarningsAsErrors` 使 restore 直接失败；`BasicTest.VeryBasicTest` 无超时等待 TaskCompletionSource，曾实测挂起 10 分钟以上被手动终止。
- 2026-09-13：完成。① MessagePack 3.1.4 → 3.1.8（当时最新稳定版），`dotnet restore --force` 带 NuGetAudit 零告警，无需 NoWarn/抑制。② `Delivered` → `Enqueued` 改名（`RoomBroadcastResult`、`RoomManager.BroadcastAsync` 局部变量、XML 注释、`docs/rooms.md`、测试）。③ RoomManagerTests 失败根因：**测试时序问题**——`BroadcastAsync` 的 `SendAsync` 只保证消息入 mailbox（经 InProcTransport 队列 + mailbox 两级队列），测试在 BroadcastAsync 返回后立即断言 inbox，与 actor 异步处理竞争；改为等待 `RecordingSessionActor.MessageProcessed`（TCS 信号）+ `WaitAsync(10s)`，非 sleep。④ 调查 Net.Tests 挂死时发现**产品代码死锁 bug**：`ActorSystem.DeliverLocalAsync` 先 `await host.Startup` 再入 mailbox，而 `ActorHost.RunAsync` 只在 start hook 返回后才消费 mailbox → 任何 actor 在自己的 start hook 里给自己发消息（`RoomSessionRouter.OnSessionStartedAsync` 的 welcome 广播正是如此）必然循环等待挂死；修复为直接入 mailbox（顺序性由 RunAsync 的消费时序天然保证，注释已写明），`--blame-hang-timeout` 定位挂死测试的判断依据即来自此。⑤ 测试侧为 Net.Tests 全部 I/O 等待（`SimpleConnection.ReceiveAsync`、`InMemorySessionConnection.ExpectAsync`、`GateServerTests` 的 Connect/Write/ReadFrame）加 10s 超时保护。⑥ CI `dotnet.yml` Test 步骤加 `--blame-hang-timeout 5m --blame-hang-dump-type mini`。最终 `dotnet test`（不关审计）两轮全绿：Core 14、Cluster 4、Extras 9、Net 5，共 32 通过 0 失败 0 跳过。
