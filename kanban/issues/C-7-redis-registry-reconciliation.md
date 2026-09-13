# C-7 RedisClusterRegistry 断线对账与自愈（借鉴成熟 registry 模式）

## Goal
不引入 etcd（与现有 Redis registry 重复建设），而是把成熟 registry 实现中三个经过验证的模式移植到 `RedisClusterRegistry`：注册自愈、订阅断线 diff 对账、完整 fake 测试基建。

## Subtasks
- [x] 自愈：注册时保留 value 副本，检测到 key（TTL）过期后按记录重新注册（条件更新防竞态）。
- [x] 订阅断线对账：pub/sub 断线重连后做一次全量读取 diff，把缺失/变更回放为合成事件投给订阅方；与下游配合做 no-op 抑制（原始 bytes 比对，未变更不触发），避免连接池/缓存抖动。
- [x] 实现完整内存 fake（`FakeRedisServer`：模拟过期、断线、重连、其他节点写入），单测不依赖真实 Redis。
- [x] keepalive/订阅循环异常处理审查：瞬时网络错误与 key 过期自愈分开，异常记日志不允许吞掉导致循环死亡。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- Redis 断线期间其他节点的注册/注销变更，在重连后被对账补偿（不丢事件）。
- 本节点 key 过期后自动重新注册成功。
- no-op 回放不触发下游事件。
- 单测不依赖真实 Redis 即可覆盖上述场景。

## Test Cases
- [x] fake 模拟：断线期间注册 A、注销 B → 重连对账产生 added A / removed B。
- [x] no-op：回放内容与缓存一致时不发事件。
- [x] key 过期 → 自愈重注册。

## Related Files / Design Docs
- `src/Skynet.Cluster/RedisClusterRegistry.cs`、`RedisClient.cs`
- `tests/Skynet.Cluster.Tests/FakeRedisServer.cs`、`CapturingLogger.cs`

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。注意参考实现的教训：某些真实客户端的断线信号未闭环，对账逻辑只被 fake 验证过——移植时必须确认 StackExchange.Redis 的 ConnectionFailed/Restored 事件真实接线。
- 2026-09-13：完成。commit `cf390ea`，验收 35/35（当时基线）。
