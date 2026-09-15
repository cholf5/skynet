# E-5 MsgServer 业务路由层

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 11.3：提供 Gate 之上的业务路由层 MsgServer——按消息首字节协议号（或用户路由函数）将客户端
消息分发到 game server actor，并支持把响应回写给会话。与现有 `ISessionMessageRouter` 机制衔接。

## 子任务
- [x] `Skynet.Net` 增加 `MsgServerRouter : ISessionMessageRouter` 与 `MsgCommandHandler` 委托（零反射、启动期注册）。
- [x] 支持注册：命令号 → 自定义处理器（`RegisterCommand`）或内置转发（`ForwardCommand`，经 `SessionContext.CallAsync` 回写客户端）；未注册命令号回错误帧。
- [x] 样例：见 `docs/msg-server.md` 与集成测试（EchoActor 请求/响应全链路演示）。
- [x] 测试：`tests/Skynet.Net.Tests/MsgServerRouterTests.cs`（11 单元测试 + 3 Gate 集成测试）。
- [x] 文档：`docs/msg-server.md`。

## 验收标准
- 客户端帧经 Gate → MsgServer → 业务 actor → 响应回写全链路打通。
- 路由表编译期/启动期注册，无反射查找。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 已注册协议号请求 | 目标 actor 收到并回包 |
| 2 | 未注册协议号 | 客户端收到错误帧 |
| 3 | 业务 actor 抛异常 | 错误帧返回，session 存活 |

## 相关文件
- `src/Skynet.Net/SessionContext.cs`（ISessionMessageRouter）
- `src/Skynet.Extras/RoomSessionRouter.cs`（现成参考）

## 开发记录
### 实现要点
- 落地于 `src/Skynet.Net/MsgServerRouter.cs`（与 `ISessionMessageRouter` 同层）：`public delegate ValueTask MsgCommandHandler(SessionContext, ReadOnlyMemory<byte>, CancellationToken)` + `sealed class MsgServerRouter : ISessionMessageRouter`。
- API 面：`RegisterCommand(byte, MsgCommandHandler)`、`ForwardCommand(byte, ActorHandle, TimeSpan? callTimeout = null)`、`IsRegistered(byte)`；命令表为 `ConcurrentDictionary<byte, MsgCommandHandler>`。
- 帧约定（已在 XML 注释与 `docs/msg-server.md` 写死）：客户端帧 = `[command byte][业务 payload]`，命令号首字节被剥掉后交给 handler；错误帧 = `[0x00][原命令号][UTF-8 错误文本]`，文本上限 256 字节并按字符边界截断。错误文本三种固定格式：`empty frame` / `unknown command 0xNN` / `handler error: ExceptionType: message`。
- 边界行为：
  - 未知命令号 → 错误帧，会话保持存活；未注册任何命令时同样如此（每帧回 `unknown command` 错误帧）。
  - handler 抛异常 → 记 Warning 日志 + 错误帧，会话保持存活（`OperationCanceledException` 在会话取消时原样上抛，不算业务错误）。
  - 重复注册同一命令号 → `ArgumentException`；命令号 `0x00` 保留给错误帧标记，禁止注册（`ArgumentException`）；首次 `OnSessionStartedAsync` 后路由表冻结，再注册抛 `InvalidOperationException`（冻结使共享实例可被多会话并发读，`RouterFactory = _ => msgServer` 即可）。
  - 空 payload 帧 → 错误帧 `[0x00][0x00]["empty frame"]`。
- `ForwardCommand` 内置转发：业务 payload 快照为 `byte[]` 发给目标 actor（不把路由器持有的缓冲外借），响应支持 `byte[]` / `ReadOnlyMemory<byte>` / `string`（UTF-8），其余类型或 null 响应转成错误帧；目标 actor 抛异常/超时同样只回错误帧。

### 取舍
- 命令号用 `byte`（PRD 11.3 "消息首字节协议号"），协议文本以英文错误串为准，与既有 Gate 生态保持一致。
- 委托用专用 `MsgCommandHandler` 而非裸 `Func<...>`，XML 文档可完整标注参数语义。
- 冻结语义选择"首个会话启动即冻结"而非并发注册：保证分发热路径无锁读取，注册期竞争属于启动期 bug，应 fail loudly。
- 处理器异常的文本包含异常类型名与消息，便于客户端排障；超长文本截断到 256 字节防止错误帧失控。
- 样例未单独新增 Example 工程（避免新建 csproj，遵循任务约束），以 `docs/msg-server.md` 示例 + 集成测试充当演示。

## Review 意见
（待填）

## QA 记录
（待填）
