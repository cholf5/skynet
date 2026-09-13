# D-5 Gate 加密：重放防护与方向密钥分离

- **来源**：D 系列 Code Review（C-6 验收），P2 × 2（密码学加固）
- **开发者**：AI Agent
- **估算复杂度**：M
- **依赖任务**：无（但注意与 D-8 无关；会改动 wire 行为，需在协议冻结前做）

## 目标

C-6 的 AES-256-GCM 帧加密算法选型正确，但存在流密码时代遗留的两个设计债，趁协议尚无存量用户时补齐：

1. **无重放防护**：加密帧无序列号、AAD 为空（`GateFrameCodec.cs:33`、`:104-139`），同一会话内捕获的合法密文帧可被原样重放且解密成功。
2. **收发方向共用会话密钥**：客户端与网关双向用同一 key（无方向分离，`ISessionFrameCipher.cs`），靠随机 nonce 避免碰撞。TLS 风格是每方向独立 key。

## 子任务

1. 帧格式加发送序号（ulong），写入 AAD；接收端维护滑动窗口去重（参照 IPsec anti-replay window），旧序号拒绝。
2. HKDF info 追加方向标记（`info += direction`，info 已含 cipherId，`GateHandshakeCodec.cs:47-61`），双向派生独立 key。
3. 文档标注随机 96-bit nonce 的 birthday bound（每密钥约 2^32 帧内安全，`GateFrameCodec.cs:108`），或改为"方向计数器 + 随机 iv 前缀"方案（若改，ADR 说明）。
4. 顺手补 nonce 唯一性测试（两次 `EncryptFrame` 必产生不同 nonce，当前无此断言）。

## 验收标准

- 同一密文帧重放被接收端拒绝（有专门测试，修复前该测试应失败）。
- 篡改序号/AAD 的帧解密失败（GCM tag 校验覆盖序号）。
- 双向密钥不同（可从派生函数单测断言）。
- 既有加密测试矩阵（`GateEncryptionHandshakeTests` 等）全绿；wire 格式变更同步更新 `docs/gate-encryption.md` 并在协议版本字段中体现。

## 测试用例

1. 重放：捕获合法帧原样重发 → 拒绝 + 连接按策略处理。
2. 乱序窗口：窗口内乱序帧被接受，窗口外旧帧被拒。
3. 方向隔离：client→gate 与 gate→client 使用不同 key（派生单测）。
4. nonce 唯一性：批量加密断言 nonce 无重复。

## 相关文件

- `src/Skynet.Net/Encryption/GateFrameCodec.cs`、`ISessionFrameCipher.cs`、`GateSessionPipeline.cs`、`GateHandshakeCodec.cs`
- `tests/Skynet.Net.Tests/Encryption/`
- `docs/gate-encryption.md`
