# E-9 Consistent hashing placement 策略

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：S
- 依赖：无

## 目标
落实 PRD 10.3 可选 placement：提供一致性哈希放置策略（`IPlacementStrategy`），供某类命名空间 actor
自动分配到节点（如 `room:<id>` → node）。默认仍为 direct/manual。

## 子任务
- [ ] `Skynet.Cluster` 增加 `IPlacementStrategy` + `ConsistentHashPlacement`（虚节点哈希环，无锁读）。
- [ ] 节点上下线时哈希环更新；测试分布均匀性与粘性（同 key 稳定映射）。
- [ ] 文档：何时用 direct vs consistent hash。

## 验收标准
- 同一 key 在节点集合不变时始终映射到同一节点；增删节点仅迁移 ~1/N 的 key（抽样断言）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 同 key 重复解析 | 结果稳定 |
| 2 | 节点集合不变 1000 key | 全部稳定 |
| 3 | 移除 1/4 节点 | 迁移比例约 25%（±10%） |

## 相关文件
- `src/Skynet.Cluster/StaticClusterRegistry.cs`
