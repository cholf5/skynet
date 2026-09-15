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
- [x] `Skynet.Cluster` 增加 `IPlacementStrategy` + `ConsistentHashPlacement`（虚节点哈希环，无锁读）。
- [x] 节点上下线时哈希环更新；测试分布均匀性与粘性（同 key 稳定映射）。
- [x] 文档：何时用 direct vs consistent hash。

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
- `src/Skynet.Cluster/IPlacementStrategy.cs`（新增）
- `src/Skynet.Cluster/ConsistentHashPlacement.cs`（新增）
- `tests/Skynet.Cluster.Tests/ConsistentHashPlacementTests.cs`（新增）
- `docs/placement.md`（新增）

## 开发记录
- **API 形态**：`IPlacementStrategy`（`Place(key)` / `UpdateNodes(nodes)` / `Nodes`）独立于
  `IClusterRegistry`，不接入 registry 主流程（按任务要求保持可组合）；`ConsistentHashPlacement`
  为首个实现。空节点集在构造与 `UpdateNodes` 时即抛 `ArgumentException`（把"无参与者"视为配置错误
  而非逐次查找失败），因此 `Place` 总能返回当前快照中的合法节点；重复节点 id 去重，null/空 id 拒绝。
- **哈希选择**：FNV-1a 64（offset basis 14695981039346656037 / prime 1099511628211，UTF-8 字节，
  与 `PayloadContractRegistry.ComputeContractId` 的 FNV-1a 32 同族、风格一致）后接 murmur3
  `fmix64` 终结器。**终结器是必需的**：裸 FNV-1a 对结构化短串（`room:123`、`node-3#42`）雪崩不足，
  实测 4 节点环上一个节点可占 >50% key（环位置与 key 位置同时聚簇）。加终结器后 4 节点份额
  23.1%–27.6%。全程确定性、非加密、无反射。
- **虚拟节点数**：默认 160（配置范围 1..10000，越界抛 `ArgumentOutOfRangeException`）。160 在小
  中型集群下各节点份额偏差仅百分之几；实测 1 虚节点时份额可到 0.8%–50.9%，故文档明确 1 仅为合法
  下界、无统计意义。所有节点必须使用相同 `virtualNodeCount`，否则环不一致（已在 docs 强调）。
- **环重建策略**：读路径完全无锁——环是不可变快照（`RingEntry[]` 按 hash+nodeId 排序 + 节点表），
  `UpdateNodes` 在调用线程上构建新快照后经 `Volatile.Write` 原子整体替换，读者 `Volatile.Read`
  取到完整旧环或完整新环；被拒绝的更新（非法 id/空集）不动当前环（有测试覆盖）。并发多次
  `UpdateNodes` 原子、最后完成的完整集合生效。查找为二分查找（首个人环位置 >= key hash 的虚节点，
  相等位置按 nodeId 序稳定决胜，尾部回绕），key 常见长度走 stackalloc 缓冲，超长走
  `ArrayPool`，查找零堆分配。
- **迁移语义（实测，4 节点/160 虚节点/4000 key）**：移除 1 节点迁移 23.92%（期望 25%）；4→5 节点
  迁移 18.50%（期望 20%）。测试断言带 ±10% 容差带，key 集与哈希均确定性，非 flaky。
- 测试：新增 `ConsistentHashPlacementTests`（13 例）：重复解析稳定、双实例（乱序输入）映射一致、
  移除/新增节点迁移比例、分布均匀、同集合重更新稳定、非法节点集（空/null/含空串/含 null）拒绝且
  不破坏现环、虚节点数非法值（0/-1/10001）拒绝、`Place` 拒绝 null/空 key、并发
  `UpdateNodes`+`Place`（4 读线程 ×25000 次 + 2 写线程 ×200 次）结果恒为合法节点且稳定。
- 全量验证：`dotnet build Skynet.sln -c Release` 0 error 0 warning；`dotnet test Skynet.sln`
  263/263 通过（基线 250 + 新增 13）。
- 备注：基线期间观察到一次 `Skynet.Net.Tests` 偶发失败（同构建 Net 模块零改动，复跑 96/96 通过），
  与本任务无关，未处理（任务约束不动 pre-existing 问题）。

## Review 意见
（待填）

## QA 记录
（待填）

## Review 意见
> PM 审查记录（2026-09-15）：
- 设计审查：FNV-1a 64 + fmix64 终结器的必要性有实测数据支撑（裸 FNV 对结构化短串份额失衡 50% → 加终结器后 23%–28%）；环为不可变快照 + Volatile 整体替换，读路径无锁；(hash, nodeId) 排序决胜保证跨进程确定性。
- 迁移比例实测（4 节点/160 虚节点/4000 key）：移除 1 节点 23.92%（期望 25%），4→5 节点 18.50%（期望 20%），断言区间合理且确定性。
- 未接入 registry 主流程、独立可组合，符合"可选策略"定位；docs/placement.md 的"何时不该用"一节写得对。
- 分支通过 fast-forward 合并了 main 的看板提交，PM 认可（无代码冲突）。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | 同 key 重复解析稳定 | ✅ |
| 2 | 节点集合不变 1000+ key 全稳定 | ✅ 4000 key |
| 3 | 移除 1/4 节点迁移 ~25% | ✅ 实测 23.92%（±10% 内） |
| 4 | 并发 UpdateNodes + 解析 | ✅ 4 读线程 ×25000 + 2 写线程 ×200 |
| 5 | 非法配置 | ✅ 空集/非法虚拟节点数抛异常 |
| 6 | PM 独立复跑全量测试 | ✅ 263/263（Skynet.sln，net10.0） |

无 P0/P1 问题。**QA Passed**
