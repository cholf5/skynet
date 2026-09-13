# C-6 移植 Gate 加密握手（RSA token 握手，现代化改造）

## Goal
为 Skynet Gate 补上"token 换会话密钥"加密握手，填补 Gate 无认证/无加密的缺口：客户端连接后经 RequestEncryptToken/ConfirmEncryptKey 双向握手派生会话密钥，业务帧加密传输。握手状态机协议无关（约 400 行实现 + 等量测试），挂在 Skynet Gate 的 TCP/WebSocket 会话建立流程上。

## Subtasks
- [x] 实现握手核心：纯函数 Codec 集合、服务端会话（IssueToken/ProcessConfirmEncryptKey/FixedTimeEquals/过期校验/防重放）、客户端会话、`IGateRsaKeyProvider` + `InMemoryGateRsaKeyProvider`。
- [x] 现代化改造 1：默认 RSA padding 采用 OAEP-SHA256。
- [x] 现代化改造 2：会话密钥派生采用 HKDF-SHA256（expand info 绑定 cipher id）。
- [x] 现代化改造 3：会话帧加密用 AES-256-GCM（经 `ISessionFrameCipher` 可插拔，握手层不绑死算法），不用 RC4。
- [x] 用手写 byte layout 替代 protobuf 信封，wire 协议自洽。
- [x] `GateServerOptions` 增加认证钩子接缝：握手成功后才创建/绑定 SessionActor 并放行业务帧（PRD 4.10 authentication hook）。
- [x] 完整握手测试（重放/过期/超时/明文开关）。

## Developer
- Owner: AI Agent
- Complexity: M

## Acceptance Criteria
- 握手完成前 Gate 不投递任何业务帧；未配置加密会话的连接被明确拒绝（错误码可诊断）。
- token 重放、过期、mismatch 均被拒绝且用 `CryptographicOperations.FixedTimeEquals` 比对。
- 默认配置使用 OAEP + HKDF + AES-GCM；明文模式需显式 opt-in。
- 测试全绿。

## Test Cases
- [x] 双端 session key 派生一致（回环握手 + 业务加密帧往返）。
- [x] token 重放 / 过期 / 未发 token 先 Confirm / 二次握手拒绝。
- [x] 握手超时故障、`EnableGateEncrypt=false` 明文路径不变。

## Related Files / Design Docs
- `src/Skynet.Net/Encryption/`
- `src/Skynet.Net/GateServer.cs`、`GateServerOptions.cs`
- `tests/Skynet.Net.Tests/Encryption/`
- `docs/gate-encryption.md`
- `docs/PRD.md` §4.10 Security

## Dependencies
- C-1 修复构建红与测试债

## Notes & Updates
- 2026-09-13：任务创建。协议无关是移植可行的关键：握手状态机本身可直接搬，帧分发层重新设计。
- 2026-09-13：完成。commit `6d929b5`，验收 99/99。`EnableEncryption` 默认 false（避免悄悄的 wire breaking change），文档标注生产应开启。
