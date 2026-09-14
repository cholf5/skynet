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
- [ ] `Skynet.Net` 或 `Skynet.Extras` 增加 `MsgServer`/`IMessageDispatcher` 抽象与默认实现。
- [ ] 支持注册：协议号 → 目标 actor/接口代理；未注册协议号回错误帧。
- [ ] 样例：GameServerActor + 客户端请求/响应演示。
- [ ] 测试：分发正确性、未知协议号、目标 actor 异常时的错误回包。
- [ ] 文档。

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
