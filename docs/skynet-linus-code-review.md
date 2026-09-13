# skynet 代码质量评审（偏 Linus 风格）

## 评审范围与证据
- 代码目录：`src/Skynet.Core`、`src/Skynet.Cluster`、`src/Skynet.Net`、`src/Skynet.Extras`、`src/Skynet.Generators`
- 测试目录：`tests/*`
- 工程文件：`.github/workflows/*`、`Directory.Build.props`、`Directory.Packages.props`
- 实测结果：
  - `Skynet.Core.Tests`：14/14 通过
  - `Skynet.Cluster.Tests`：4/4 通过
  - `Skynet.Extras.Tests`：出现失败（`RoomManagerTests.BroadcastAsyncDeliversPayloadToAllMembers`）
  - `Skynet.Net.Tests`：`BasicTest.VeryBasicTest` 挂起并导致 test host 中止（已由 `--blame-hang` 复现）

## 优点（具体实现）
1. 模块边界清晰，不是大泥球。
- `Core/Cluster/Net/Extras/Generators` 分层基本合理，职责分离明确。
- `ActorSystem` + `ITransport` + `IClusterRegistry` 的抽象组合，扩展点位置对了。

2. Actor 串行语义实现得靠谱。
- `ActorHost` 使用 `Channel` 单读者模型（`SingleReader = true`），配合集中 `ProcessMessageAsync`，顺序性基础扎实。
- 错误回调与指标采集都挂在同一处理路径，观测点不分叉。

3. 工程基本盘比大多数“玩具框架”好。
- 有 CI、有 release workflow、有集中依赖版本管理（`Directory.Packages.props`）。
- `TreatWarningsAsErrors`、`EnforceCodeStyleInBuild` 开启，至少在态度上是工程化的。

4. Source Generator 方向正确。
- 通过 `[SkynetActor]` 生成 proxy/dispatcher，减少手写胶水，接口化 RPC 的 DX 方向是对的。

## 致命问题（有）
1. 跨节点消息序列化设计存在硬伤：靠运行时 `Type.GetType` 还原 payload 类型。
- 证据：`src/Skynet.Core/Serialization/MessageEnvelopeSerializer.cs:69`
- 问题本质：
  - 强绑定程序集限定名，节点间版本轻微漂移就可能直接炸。
  - 反射型类型恢复扩大攻击面，远端可构造类型名触发反序列化路径风险。
  - 与项目“强类型/生成代码优先”的方向冲突。
- 改进建议：
  - 用显式 `MessageTypeId -> formatter/contract` 映射表（生成期产物），禁止自由类型名反射解析。
  - Envelope 只传 `contract id + method id + payload bytes`，不要传 `AssemblyQualifiedName`。

2. 网络测试不稳定到会挂死测试主机，说明生命周期控制存在明显薄弱点。
- 证据：`tests/Skynet.Net.Tests/BasicTest.cs` 的 `VeryBasicTest`；`--blame-hang` 显示该用例未完成。
- 问题本质：
  - 用例等待 `TaskCompletionSource` 无超时保护，任何欢迎消息路径异常都会无期限阻塞。
  - 这种阻塞能拖垮整个测试进程，说明“失败快速反馈”机制缺失。
- 改进建议：
  - 所有 I/O 测试必须 `WaitAsync(timeout)`，并输出上下文日志。
  - 给 Session/Gate 增加可观测的启动与首包事件，避免黑箱等待。

3. “Delivered” 指标语义不真实，测试已暴露这个设计裂缝。
- 证据：`src/Skynet.Extras/RoomManager.cs:109-111` 在 `SendAsync` 返回后直接 `delivered++`；对应 `RoomManagerTests` 失败。
- 问题本质：
  - 当前 `SendAsync` 只保证入队，不保证对端处理完成。
  - 你把“已入队”包装成“已送达”，这是指标污染。
- 改进建议：
  - 把计数字段改名为 `Enqueued`；若要 `Delivered`，必须引入确认路径（ack/回执/可选可靠投递层）。

## 一般问题（可接受但不优雅）
1. 代码风格一致性不稳定。
- 多处缩进和排版明显跑偏（如 `ActorRef.cs`、`GateServerTests.cs`、`TcpTransportTests.cs`）。
- 不会立刻炸，但会持续抬高维护成本。

2. TcpTransport 缺少帧大小上限防护。
- 证据：`src/Skynet.Cluster/TcpTransport.cs:547,557` 仅检查负值，然后按长度分配数组。
- 风险：恶意长度可触发大内存分配，直接 DoS。
- 建议：增加 `MaxFrameBytes` 并在解帧前硬拒绝。

3. 生成代码允许同步阻塞调用。
- 证据：`src/Skynet.Generators/SkynetActorGenerator.cs:430,453` 存在 `.GetAwaiter().GetResult()`。
- 风险：在线程上下文复杂场景里容易制造阻塞与吞吐抖动。
- 建议：限制 RPC 合约为 `Task/Task<T>/ValueTask/ValueTask<T>`，禁用同步返回方法。

4. SDK 与发布链路版本策略偏保守且过时。
- 证据：CI 固定 `9.0.100-rc.1`（`.github/workflows/dotnet.yml:19`, `release.yml:28`）。
- 风险：工具链差异导致“本地过、CI不过”或反过来。
- 建议：尽快切到稳定 SDK（或至少统一到同一 patch/rc）。

## 信息不足（明确缺失，不做臆测）
1. 没有看到覆盖率报告结果与阈值门禁，无法确认“核心模块 >=80%”是否达标。
2. 没有可复现的性能基准数据（吞吐、延迟、长稳压测），无法验证性能目标。
3. 没有看到 TLS、鉴权、限流在真实部署路径中的端到端测试证据。

## 是否值得学习
**结论：有条件值得学习。**
- 值得学的是：模块拆分、Actor 串行执行模型、生成器驱动的 RPC 体验。
- 不值得学的是：运行时反射型 payload 解析、指标语义偷换、测试防挂策略缺失。

## 是否适合用于生产
**结论：当前不适合作为通用生产框架；仅适合受控内网、小规模、可快速修补的试点场景。**
- 可用场景：内部 PoC、教学、单团队受控服务。
- 不建议场景：跨团队共享基础设施、互联网暴露面服务、强 SLA 游戏核心链路。

## 评分
- **评分：5.8 / 10**
- **同类项目水平：中（偏下）**

一句话总结：方向对了，但你在“消息类型边界、测试可靠性、网络防护”这三件基础工程上留了硬洞；这些洞不补，谈不上生产级。
