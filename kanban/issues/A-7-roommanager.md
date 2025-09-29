# A-7 RoomManager & Multicast Sample

## Goal
实现房间管理与广播功能，提供多客户端共享房间的示例。

## Subtasks
- [ ] 设计 RoomManager 数据结构与 API（Join/Leave/GetMembers/Broadcast）。
- [ ] 实现 Multicast 机制，确保广播消息高效推送给所有成员。
- [ ] 集成 Gate/Session，演示客户端加入房间并接收广播。
- [ ] 为 RoomManager 编写单元测试验证成员管理与广播正确性。
- [ ] 为房间广播场景编写集成测试与性能基准。
- [ ] 更新文档记录示例运行步骤与扩展建议。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 多个 SessionActor 可加入同一房间并接收广播消息，无重复或遗漏。
- RoomManager 支持查询房间成员并处理成员离线清理。
- 性能基准满足 PRD 中对吞吐与延迟的基本要求。

## Test Cases
- [ ] `dotnet test`（含 RoomManager 单元测试）
- [ ] 集成测试：多个 Session 模拟广播
- [ ] 性能基准脚本

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-6 GateServer & Session Pipeline

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
