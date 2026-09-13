# C-7 RedisClusterRegistry 断线对账与自愈（借鉴 EtcdManager 模式）

## Goal
不引入 etcd（与现有 Redis registry 重复建设），而是把 Sharpest `EtcdManager` 中三个经过验证的模式移植到 `RedisClusterRegistry`：注册自愈、订阅断线 diff 对账、完整 fake 测试基建。

## Subtasks
- [ ] 自愈：注册时保留 value 副本，检测到 key（TTL）过期后按记录重新注册（对应 `EtcdManager.RecoverLeaseAsync` 的条件更新防竞态写法）。
- [ ] 订阅断线对账：pub/sub 断线重连后做一次全量读取 diff，把缺失/变更回放为合成事件投给订阅方；与下游配合做 no-op 抑制（原始 bytes 比对，未变更不触发），避免连接池/缓存抖动（参考 `EtcdManager.ReconcileWatchAsync` + `ServiceDiscovery` 的 no-op 抑制组合）。
- [ ] 实现 `FakeRedisSubscriber` 式完整内存 fake（模拟过期、断线、重连），替代当前对真实 Redis 的依赖测试。
- [ ] keepalive 循环异常处理审查：瞬时网络错误与过期自愈分离，避免吞异常掩盖问题。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- Redis 断线期间其他节点的注册/注销变更，在重连后被对账补偿（不丢事件）。
- 本节点 key 过期后自动重新注册成功。
- no-op 回放不触发下游事件。
- 单测不依赖真实 Redis 即可覆盖上述场景。

## Test Cases
- [ ] fake 模拟：断线期间注册 A、注销 B → 重连对账产生 added A / removed B。
- [ ] no-op：回放内容与缓存一致时不发事件。
- [ ] key 过期 → 自愈重注册。

## Related Files / Design Docs
- `src/Skynet.Cluster/RedisClusterRegistry.cs`（现有 TTL 续期 L251-261、pub/sub L44、本地缓存 L457）
- `E:\dev\cholf5\Sharpest\src\Sharpest.ServerKit\Etcd\EtcdManager.cs`（RecoverLeaseAsync L110-139、ReconcileWatchAsync L251-282）
- `E:\dev\cholf5\Sharpest\src\Sharpest.MicroKit\Discovery\ServiceDiscovery.cs`（no-op 抑制 L161-165）
- `E:\dev\cholf5\Sharpest\tests\Sharpest.Tests\ServerKit\EtcdManagerTests.cs`（fake 基建参考）

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。注意 Sharpest 的 EtcdClient 真实断线信号未闭环（onDisconnect 生产路径从不触发），对账逻辑只被 fake 验证过——移植时同步确认 StackExchange.Redis 的 ConnectionFailed/Restored 事件可用，别重蹈覆辙。
