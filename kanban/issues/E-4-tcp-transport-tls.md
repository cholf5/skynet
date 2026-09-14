# E-4 跨节点 TLS（TcpTransport 可选 SslStream 加密）

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 4.10 "Transport should support TLS (optional) for cross-node"。为 `Skynet.Cluster.TcpTransport`
增加可选 TLS（SslStream）传输加密：服务端与客户端各配置证书选项，默认关闭保持明文兼容。

## 子任务
- [ ] `TcpTransportOptions` 增加 `SslServerOptions` / `SslClientOptions`（X509Certificate2 + 校验回调）。
- [ ] 连接建立后按需协商 SslStream（配置不一致时 fail-fast 报协议错误）。
- [ ] 测试：自签证书明文/加密互通、加密↔明文互拒、带 client cert 校验。
- [ ] 文档：`docs/transport.md` 增补 TLS 章节。

## 验收标准
- 默认关闭时行为与现状一致；开启后跨节点 CallAsync 在 TLS 上正常工作。
- 握手失败（证书不匹配）产生明确错误而非挂起。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 双端 TLS 自签证书 + 信任回调 | 远程 CallAsync 成功 |
| 2 | 一端 TLS 一端明文 | 连接失败且错误可观测 |
| 3 | 客户端证书校验拒绝 | 连接关闭并记录日志 |

## 相关文件
- `src/Skynet.Cluster/TcpTransport.cs`
- `docs/transport.md`
