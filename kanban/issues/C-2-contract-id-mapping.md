# C-2 Contract ID 映射表：消灭远端 Type.GetType 反射

## Goal
远程消息 payload 的类型还原从 `AssemblyQualifiedName + Type.GetType(throwOnError: true)`（`MessageEnvelopeSerializer.cs:54,69`）改为编译期生成的 `contract id → Type + IMessagePackFormatter` 静态映射表。envelope 线上只传 contract id + payload bytes。一次解决两个硬伤：远端可诱导反射加载任意类型（安全）与节点间版本漂移即炸（脆弱）。

## Subtasks
- [ ] 在 `Skynet.Generators` 中收集所有 MessagePack 合约类型（含 SourceGenerator 生成的 `*_Request` payload 类型），生成静态映射表（id ↔ Type/formatter）。
- [ ] 改造 `SerializedMessageEnvelope`：`PayloadType` 字符串字段替换为 `PayloadContractId`（int/ushort），保留字段号策略以尽量兼容或明确 break 并 bump `Version`。
- [ ] 序列化/反序列化路径查表代替 `Type.GetType`；查不到时抛明确异常并拒绝投递（记录日志）。
- [ ] 处理跨版本场景：未知 contract id 的错误路径 + 集成测试（两节点不同 id 表）。
- [ ] 评估并记录与 MessagePack 官方 source generator / 动态 resolver 的冲突风险（参考 Sharpest 的 duplicate-key 崩溃教训，必要时在 `Directory.Build.targets` 禁用传递 analyzer）。
- [ ] 更新 `docs/architecture.md` 中 wire 协议章节。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 代码库中不再存在 `Type.GetType` 用于 payload 类型还原（可 grep 验证）。
- 跨节点 send/call 集成测试全绿，行为与现状一致。
- 恶意/未知 `PayloadContractId` 的帧被拒绝并记日志，进程不崩溃。

## Test Cases
- [ ] 远程 call 往返（现有 TcpTransportTests 扩展）。
- [ ] 未知 contract id 拒绝路径单测。
- [ ] 两节点 id 表不一致的集成测试。

## Related Files / Design Docs
- `src/Skynet.Core/Serialization/MessageEnvelopeSerializer.cs`
- `src/Skynet.Core/Serialization/SkynetCoreMessagePackResolver.cs`
- `src/Skynet.Generators/SkynetActorGenerator.cs`
- `E:\dev\cholf5\Sharpest\src\Sharpest.MsgPackGen\`（contract id 思路参考）
- `E:\dev\cholf5\Sharpest\Directory.Build.targets:18-41`（官方 generator duplicate-key 教训）

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。这是上一轮评审认定的"致命问题 #1"，也是 Sharpest 借鉴清单中优先级最高的一项。
