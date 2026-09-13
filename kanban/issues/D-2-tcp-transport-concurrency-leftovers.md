# D-2 TcpTransport 并发遗留三连（ABA 删除 / 连接失败 fail-fast / MessageId 撞车）

- **来源**：D 系列 Code Review（C-3 hardening 的未覆盖项），3 × P1
- **开发者**：AI Agent
- **估算复杂度**：M
- **依赖任务**：无（与 D-3 同主题，可同批做）

## 目标

C-3 修复了"已建立连接的死亡"路径，但 TcpTransport 的连接生命周期与响应匹配还存在三个并发/正确性缺陷，全部在一次 hardening 主题内补齐。

## 缺陷清单（Review 证据）

1. **ABA 连接字典误删（P1）**：`src/Skynet.Cluster/TcpTransport.cs:602` read loop finally 无条件 `_connections.TryRemove(_remoteNodeId, out _)`。旧连接 A 死亡的同时 `EnsureConnectionAsync` 可能已写入健康新连接 B，A 的 finally 会把 B 删掉，之后第三次拨号产生 C，与仍存活的 B 形成双活链路。
2. **连接建立失败时 pending 永久悬挂（P1）**：`TcpTransport.cs:105-113` pending call 在 `EnsureConnectionAsync` 之前注册，而 connect 失败（ConnectTimeout、节点拒绝）不 fail-fast 挂起的 pending——调用方 TCS 永不完成。**KCP 版已做对**（`KcpTransport.cs:138-151` 在 EnsureConnection 失败时 `TrySetException`），照抄即可。
3. **MessageId 跨节点裸计数撞车（P1）**：`TcpTransport.cs:306` 响应匹配仅凭 `envelope.MessageId`。两节点 message id 计数器各自从 0 递增、无节点标识参与；双向并发 Call 下，对端**新请求**的 MessageId 与本端 pending 调用 id 撞车时，请求被误当响应完成（错误结果），且该请求被静默吞掉。

## 子任务

1. finally 改条件删除：`TryRemove(key, out var removed) && ReferenceEquals(removed, this)`。
2. `SendAsync` 中 `EnsureConnectionAsync` 失败路径 fail-fast 该次 pending（对齐 KCP 版），并确认批量场景（并发 connect 失败）无泄漏。
3. pending 匹配加作用域：pending key 改为 `(nodeId, messageId)` 或在 envelope 携带方向标记，请求帧绝不匹配 pending 表；补双向并发回归测试。

## 验收标准

- 旧连接死亡与新连接建立并发时，字典中始终是健康连接（压力测试无双活）。
- connect 失败时所有该次调用以 `RemoteConnectionClosedException`（或专用异常）快速失败。
- 双节点双向并发 Call（各 100 路）全部结果一一对应，无串扰。

## 测试用例

1. ABA：断开 A 与 `EnsureConnectionAsync` 建新连接并发执行 → 断言字典内连接引用未变。
2. connect 拒绝：指向不可达端口 → `CallAsync` 在 ConnectTimeout 内抛出，`_pendingCalls` 清空。
3. 撞车：A、B 同时互发 Call 且 message id 有意对齐 → 双方结果正确（该用例在修复前应能复现串扰）。

## 相关文件

- `src/Skynet.Cluster/TcpTransport.cs`
- `tests/Skynet.Core.Tests/TcpTransportTests.cs`（+417 行已有基础）
- 参照：`src/Skynet.Cluster/Transport/Kcp/KcpTransport.cs:138-151`

## Review 记录

- **规格审查**：✅ 通过。关键裁决：实现者认为卡片建议的 `(nodeId, messageId)` 方案**不可行**（对端发来的请求与本端发往对端的 pending 共享同一 nodeId 键空间，计数对齐时仍撞车）——审查者独立推演确认论断成立，采用卡片方案 (b) wire 方向标记属规格内设计。`WireVersion` 2→3 的 bump 有实质必要（不 bump 则混合版本集群静默带病运行），与 C-2 的"不兼容版本响亮失败"策略一致。红-绿实证：还原旧匹配逻辑后双向并发测试必红。
- **质量审查第一轮**：需修复后批准。Important：① KCP 侧 ABA 修复不对称（守卫后仍无条件 TryRemove）；② TCP `FailPendingCallsForNode` 未被条件删除结果门控，窗口内误杀新连接 pending；③ "无主响应丢弃"分支零测试覆盖。
- **修复（commit 062046f；中途因配额中断一次，由接手代理完成）**：统一为"pending 携带连接引用（`PendingCall.Connection`，connect 成功后赋值）+ `TryRemove(KeyValuePair)` 原子条件删除 + 按连接引用逐条条件 fail"——窗口问题被结构性消除；补 TCP/KCP 无主响应测试各一条（前代理的 TCP 测试有"只握手不启动读循环"缺陷，已修）；Minor 全做（OCE 过滤、异常带内因、反射降 internal、lockstep 计数器对齐断言、v2 单向静默说明）。
- **复审**：✅ 批准（三项独立验证 + 新模型窗口/互斥/KCP 对称性/C-3 回归路径全部推演闭环；全量 142/142）。
- **合入**：分支 `task/d2-tcp-transport-concurrency`（40d121d + 062046f）已 merge 到 main；**wire v3 要求集群同步升级**。
- **遗留 P2**（不阻塞）：KCP 无主响应测试的 `NotContain "Failed to deliver message"` 断言是空转的（回归时走的是 TrySetException 分支，该日志根本不会出现）→ 已记入 D-7；`TcpConnection` 嵌套类声明缩进仍多一层 Tab → 已记入 D-7；KCP outbound 握手超时后会话可被对端迟到握手应答以 inbound 身份复活（C-9 既有行为）→ 已记入 D-3。
