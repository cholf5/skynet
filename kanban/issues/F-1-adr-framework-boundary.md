# F-1 ADR-0002 框架边界与生态引用策略（含 Extras 身份标注）

## 状态
- 列：Done
- Owner: AI Agent（PM 执行）
- 复杂度：S
- 依赖：E 系列全部完成

## 目标
把"框架到此为止"的架构决策固化为 ADR-0002，防止未来的贡献者/agent 让底层重新膨胀：
1. 本仓库边界 = 框架本体（Actor 运行时、传输、RPC、Gate、必要插件接缝）。
2. 准入判据：只收"有唯一合理解"的东西；其余走插件接缝 / 生态包 / 文档模式指南。
3. 生态项目（如 OpenMU 移植）以 **git submodule + 工程引用** 方式消费框架（决策已定，不走 NuGet）。
4. 公开发 NuGet 的时机由维护者酌情决定，非当前目标；打包基础设施保留。
5. Core 依赖与公共 API 纪律。
6. Skynet.Extras 定位标注：可选参考实现，不是框架的一部分。

## 子任务
- [ ] `docs/adr/0002-framework-scope-and-ecosystem-boundary.md`
- [ ] README / docs/overview.md 增补 Extras"参考实现"身份说明

## 验收标准
- ADR 格式与 ADR-0001 一致（状态/日期/关联任务/问题陈述/决策/后果）。
- 覆盖上述 6 点决策，含"查漏补缺三分类"分流规则。

## 测试用例
文档任务，无测试；QA = 评审内容完整性与格式一致性。

## 相关文件
- `docs/adr/0001-actor-reentrancy-semantics.md`（格式基准）
- `README.md`、`docs/overview.md`

## 开发记录
- ADR-0002 落笔于 docs/adr/0002-framework-scope-and-ecosystem-boundary.md，格式对齐 ADR-0001。
  六项决策全部成文：仓库边界、唯一合理解准入判据、submodule 引用定案（含"公开发包非当前目标、
  基础设施保留"的表述）、查漏补缺三分类分流表、Core 依赖/API 纪律、Extras 参考实现身份。
- 文末保留再评估触发条件（契约级缺口通道），避免 ADR 被读成"永远不许改"。
- docs/overview.md 新增"范围边界"节；README 文档索引补两篇 ADR 链接 + 核心能力后置范围声明。

## Review 意见
- 自审：六点决策与两轮讨论的口径一致（边界/判据/submodule/发包时机/三分类/Extras），
  无新增未讨论的实质决策；ADR-0001 关联的 D-4 语义未被本文触碰。
- 链接核对：README → docs/adr/…、overview → adr/… 相对路径均有效。
- 结论：**通过**。

## QA 记录
| # | 用例 | 结果 |
|---|------|------|
| 1 | ADR 格式与 0001 一致 | ✅ 状态/日期/关联任务/问题陈述/决策/后果 |
| 2 | 六点决策完整性 | ✅ 逐条对照任务卡 |
| 3 | 文档链接可解析 | ✅ 两处相对路径核对 |

文档任务，无 P0/P1。**QA Passed**
