# A-8 DebugConsole & Observability Hooks

## Goal
提供调试控制台与基础可观测性指标，便于运维排查与性能监控。

## Subtasks
- [ ] 设计 DebugConsole 接口（Telnet/HTTP）与命令体系（list/info/trace/kill）。
- [ ] 实现基础指标采集（队列长度、处理耗时、消息计数、异常计数）。
- [ ] 集成日志与追踪上下文（TraceId）传递。
- [ ] 编写单元测试覆盖指标采集逻辑与命令解析。
- [ ] 提供运维文档，说明如何启用/配置安全策略。
- [ ] 编写示例展示控制台交互与指标导出。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- DebugConsole 可列出当前 Actor、查看信息、触发跟踪与终止。
- Metrics 可对外暴露（Prometheus/日志）并覆盖核心指标。
- TraceId 在消息处理链路中保持一致。

## Test Cases
- [ ] `dotnet test`（覆盖指标与控制台）
- [ ] 手动示例验证命令与指标导出

## Related Files / Design Docs
- `docs/PRD.md`
- `AGENTS.md`

## Dependencies
- A-7 RoomManager & Multicast Sample

## Notes & Updates
- 2025-09-29：任务创建，等待排期。
