# B-3 Documentation — Architecture Deep Dive

## Goal
撰写 Skynet 架构解读文档，介绍各层职责、消息流转与扩展指引，帮助贡献者与使用者理解系统设计。

## Subtasks
- [x] 绘制文本结构图与分层说明。
- [x] 总结核心组件职责与交互过程。
- [x] 给出扩展建议、部署拓扑与参考文档链接。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- `docs/architecture.md` 讲解核心组件、Transport、Registry、GateServer 等模块。
- 文档包含消息流转过程与扩展指引表格。
- README 文档索引链接到该文档。

## Test Cases
- [x] 人工审阅 Markdown 排版与链接有效性。

## Related Files / Design Docs
- `docs/architecture.md`
- `README.md`
- `docs/overview.md`

## Dependencies
- B-1 Documentation — Project Overview

## Notes & Updates
- 2025-09-30：完成架构文档初稿，涵盖 ActorSystem、Transport 与可观测性说明。
