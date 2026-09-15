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
- [x] `TcpTransportOptions` 增加 TLS 配置（服务端 `TlsServerCertificate` + 客户端 `UseTls` 开关与 `RemoteCertificateValidationCallback` 校验回调）。
- [x] 连接建立后按需协商 SslStream（配置不一致时在 ConnectTimeout 内 fail-fast）。
- [x] 测试：双端 TLS 自签证书互通、加密↔明文互拒（双向）、客户端证书校验拒绝。
- [x] 文档：`docs/transport.md` 增补 TLS 章节。

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
- `tests/Skynet.Core.Tests/TcpTransportTlsTests.cs`
- `docs/transport.md`

## 开发记录
- 2026-09-15：实现完成，`dotnet build -c Release` 0 warning / 0 error（TreatWarningsAsErrors 下），`dotnet test Skynet.sln` 220/220 通过（216 既有 + 4 新增，全部在 `Skynet.Core.Tests/TcpTransportTlsTests.cs`）。

### 实现要点
- 配置面（`TcpTransportOptions`，全部默认关闭/空，明文路径零行为变化）：
  - `TlsServerCertificate`（`X509Certificate2?`，服务端/入站）：设置后 accepted 连接在集群握手前以服务端身份完成 SslStream 握手。
  - `UseTls`（`bool`，客户端/出站）：出站连接在发送任何集群握手帧（0x01）前完成客户端 SslStream 握手。
  - `RemoteCertificateValidationCallback`（仅出站）：校验对端证书；null 走平台默认链/名称校验。
- TLS 按连接方向独立协商：只拨不收的节点只需 `UseTls`，只收不拨的只需证书，全互连 mesh 通常两者都配。
- 接线点：`TcpConnection.InitializeAsync` 开头（出站/入站各自先做 TLS 再走集群握手），`ConnectAsync` 按 dial 解析 `SslClientAuthenticationOptions`（`TargetHost` = registry endpoint 地址），`AcceptLoop` 传入 `TlsServerCertificate`。`TcpConnection._stream` 由 `NetworkStream` 泛化为 `Stream`，TLS 成功后替换为 `SslStream`；心跳/帧解析/重连逻辑完全复用（重连必然新建连接 → 重新 TLS 握手）。
- TLS 握手在 `ConnectTimeout` 预算内（`connectCts.CancelAfter` 覆盖 connect + TLS + 集群握手全过程）。
- `DisposeCore` 增加 `_stream.Dispose()`（SslStream 内层 stream 一并释放）。

### Fail-fast 语义（任务卡要求"配置不一致必须快速失败"）
- TLS 客户端 ↔ 明文服务端：服务端把 TLS ClientHello 解析成帧头（type 0x16，长度 ~50MB）→ 超过 `MaxFrameBytes`（默认 16MB）立即 IOException 断连；客户端握手在读到 EOF 后失败。`MaxFrameBytes = int.MaxValue`（帧保护关闭）时由 `ConnectTimeout` 兜底。
- 明文客户端 ↔ TLS 服务端：服务端 SslStream 把集群握手帧按非法 TLS record 拒绝并断连；客户端等待握手应答时读到 EOF 失败（同样有 `ConnectTimeout` 兜底）。
- 两个方向实测均在毫秒级失败；调用方拿到原始握手异常（`IOException` / `AuthenticationException`），pending call 被 drain，服务端记 warning 日志（`Failed to process incoming connection`）。

### 设计取舍
- 没有采用任务卡草拟的 `SslServerOptions`/`SslClientOptions` 复合对象，而用三个平铺属性：语义按"连接方向"（入站=服务端、出站=客户端）划分，面更小且与现有 options 风格一致；XML 文档写清方向语义与不一致时的行为。
- 不支持 mTLS（服务端不请求客户端证书）、不暴露协议版本/密码套件旋钮（平台默认 TLS 1.2/1.3）——已写入 docs 限制节。`TargetHost` 使用 registry 的 IP endpoint，基于名称的校验需要 DNS registry 或回调，也在 docs 说明。
- 服务端握手用 `SslServerAuthenticationOptions` 重载而非 `X509Certificate` 重载（后者在 net10 无带 CancellationToken 的版本）。

### 遇到的坑
- net10 下 `new X509Certificate2(byte[])` 触发 SYSLIB0057（TreatWarningsAsErrors 会挂构建）：测试证书改用 `X509CertificateLoader.LoadPkcs12` 做 PFX 导入（仍满足"导出 PFX 再导入以脱离临时密钥"的 macOS 要求）。
- 断言最初写错面：以为调用方会看到 `SendAsync` 里 pending 的包装异常（"Failed to establish a connection..."），实测 `ActorSystem.CallAsync` 先 await 传输发送任务，调用方拿到的是原始握手异常；测试改为按实际可观测面断言（类型 + 服务端日志）。
- 自查发现一次 Edit 把 `TcpConnection` 类声明/字段/构造器整块多缩进了一级 Tab（构建不报错），已恢复与文件其余部分一致。

### 顺手修复（在本次触碰的路径内）
- `ConnectAsync`：TLS/集群握手失败后原先不释放连接（socket 泄漏直到 GC）。TLS 让该失败路径从"罕见"变"常规"（双向不一致场景必经），故在 InitializeAsync 失败时 dispose 连接再抛出。
- 遗留（未修，pre-existing）：`ConnectAsync` 中 `client.ConnectAsync` 本身失败（如端口拒绝）时 `TcpClient` 同样泄漏、`connectCts` 未 Dispose；与 TLS 无关，未动。

### 测试可观测性说明
- 测试 1 通过回调捕获对端证书指纹与 `SslPolicyErrors`，证明链路确实协商了 TLS 且服务端出示了配置的证书（而非悄悄明文成功）。
- 所有等待均有界（`WaitAsync`/`WaitForLogAsync`/xUnit 超时），端口动态分配，无 sleep 时序假设；新增测试连续 5 次运行全绿。
- 明文回归由既有 216 个测试全绿证明（`TcpTransportTests` 全部用例均未配置 TLS，行为与 main 一致）。

### Review 修复
- 2026-09-15（R1，PM review 提出）：`AcceptLoopAsync` 入站处理中 `InitializeAsync` 抛异常时 catch 只做了 `client?.Dispose()`，`TcpConnection` 持有的 `_cts`、`_writeLock` 等一次性资源泄漏——每个 TLS 握手失败/被拒的入站连接漏一次。修复：`connection` 提升到 try 外声明，catch 中非空时 `await connection.DisposeAsync()`（与出站 `ConnectAsync` 失败路径同一套释放逻辑，DisposeCore 幂等且已包含 `_stream`（含 SslStream）与 `_client`，未写第二份释放代码），构造器自身失败（connection 尚未创建）时保留 `client?.Dispose()` 兜底；warning 日志保留。该路径由既有测试 2（明文客户端↔TLS 服务端）与测试 3（客户端拒证书，断言 "Failed to process incoming connection" 日志）覆盖。修复后全量 220/220 通过，新增 TLS 测试 3 次复跑全绿。

### Review/QA
（待填）
