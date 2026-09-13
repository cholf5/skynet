# C-6 移植 Gate 加密握手（RSA token 握手，现代化改造）

## Goal
从 Sharpest 移植"token 换会话密钥"加密握手到 Skynet Gate，补齐 Gate 无认证/无加密的缺口：客户端连接后经 RequestEncryptToken/ConfirmEncryptKey 双向握手派生会话密钥，业务帧加密传输。握手状态机协议无关（约 400 行 + 286 行测试），可挂在 Skynet Gate 的 TCP/WebSocket 会话建立流程上。

## Subtasks
- [ ] 移植握手核心：`GateEncryptionCodec`（纯函数集合）、`GateEncryptionServerSession`（IssueToken/ProcessConfirmEncryptKey/FixedTimeEquals/过期校验/防重放）、`GateEncryptionClientSession`、`IGateRsaKeyProvider` + `InMemoryGateRsaKeyProvider`（来源 `E:\dev\cholf5\Sharpest\src\Sharpest.Common\Micro\`）。
- [ ] 现代化改造 1：默认 RSA padding 从 PKCS#1 v1.5 改为 OAEP-SHA256（Sharpest 保留 PKCS1 仅为兼容老协议）。
- [ ] 现代化改造 2：会话密钥派生从 `SHA1(seed)` 升级为 HKDF-SHA256（评估 wire 兼容需求后定案；如需保留 SHA1 路径仅作兼容选项）。
- [ ] 用 MessagePack/pure bytes 替代 `EncryptKey`/`EncryptToken` protobuf 信封（约 20 行手写 layout），剥离 `Sharpest.Proto` 依赖。
- [ ] `GateServerOptions` 增加认证钩子接缝：握手成功后才创建/绑定 SessionActor 并放行业务帧（PRD 4.10 要求的 authentication hook）。
- [ ] 会话帧加密：评估 RC4 替换方案（ChaCha20-Poly1305 / AES-GCM），至少保证默认路径为现代算法；帧加密层做可插拔。
- [ ] 移植 286 行握手测试（完整握手、key 派生断言、token 重放拒绝、过期拒绝、开关关闭明文路径、握手超时）。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 握手完成前 Gate 不投递任何业务帧；未配置加密会话的连接被明确拒绝（错误码可诊断）。
- token 重放、过期、mismatch 均被拒绝且用 `CryptographicOperations.FixedTimeEquals` 比对。
- 默认配置使用 OAEP + HKDF/现代会话加密；明文模式需显式 opt-in。
- 测试全绿。

## Test Cases
- [ ] 双端 session key 派生一致（回环握手 + 业务加密帧往返）。
- [ ] token 重放 / 过期 / 未发 token 先 Confirm / 二次握手拒绝。
- [ ] 握手超时故障、`EnableGateEncrypt=false` 明文路径不变。

## Related Files / Design Docs
- `E:\dev\cholf5\Sharpest\src\Sharpest.Common\Micro\GateEncryptionCodec.cs`、`GateEncryptionServerSession.cs`、`GateEncryptionClientSession.cs`、`GateRsaKeyProvider.cs`
- `E:\dev\cholf5\Sharpest\tests\Sharpest.Tests\Common\Micro\MicroGateHandshakeTests.cs`
- `src/Skynet.Net/GateServer.cs`、`GateServerOptions.cs`、`SessionActor.cs`
- `docs/PRD.md` §4.10 Security

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。Sharpest 借鉴清单第一梯队组件之二。协议无关是移植可行的关键：真正绑死 MicroGate 的只有消息 id 1/2 的帧分发，状态机本身可直接搬。
