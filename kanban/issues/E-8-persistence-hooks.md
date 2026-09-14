# E-8 Persistence hooks（snapshot/WAL 插件点 + 样例）

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 4.9 可选项：提供 actor 状态持久化钩子 API（`IActorSnapshotStore`：Save/Load snapshot，
可选 WAL 追加日志），框架默认不启用；附带一个文件系统实现样例与有状态 actor 演示。

## 子任务
- [ ] `Skynet.Core` 定义 `IActorSnapshotStore` / `ISnapshotable` 钩子接口。
- [ ] `Skynet.Extras` 提供 `FileSnapshotStore` 样例实现 + 有状态 actor 重启恢复示例。
- [ ] 测试：快照保存/加载 roundtrip、损坏快照的容错。
- [ ] 文档说明"持久化是插件，不启用则零开销"。

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
