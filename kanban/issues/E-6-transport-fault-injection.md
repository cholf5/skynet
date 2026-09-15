# E-6 传输层故障注入与 Chaos 测试

## 状态
- 列：Backlog
- Owner: AI Agent
- 复杂度：M
- 依赖：无

## 目标
落实 PRD 13.3 Chaos 测试：为 TCP/KCP 传输层提供可注入故障的包装（延迟、丢包、断连、乱序），
并建立节点 kill/restart 的集成测试套件，验证 CallAsync 超时语义与恢复能力。

## 子任务
- [x] 故障注入组件 `FaultInjectingStream`（帧级丢包、写延迟、手动断连、半开读停滞）+ `TcpTransportOptions.StreamDecorator` 注入 hook（默认关闭）。
- [x] 集成测试：持续丢包、延迟抖动保序、半开连接死链检测、手动断连恢复、节点重启（故障注入下）。
- [ ] （可选）进程级 chaos 编排脚本。（未做：组件级注入已覆盖任务卡场景，进程级编排留待后续任务）

## 验收标准
- 故障注入下无消息损坏；节点恢复后调用可继续；所有断言超时有界（测试不挂死）。

## 测试用例
| # | 用例 | 预期 |
|---|------|------|
| 1 | 20% 丢包 + 重传层 | 最终送达、无重复副作用 |
| 2 | 远端 kill → restart | CallAsync 期间超时报错，恢复后成功 |
| 3 | 延迟抖动 | 无乱序副作用（按序处理语义保持） |

## 相关文件
- `tests/Skynet.Transport.Kcp.Tests/Reliable/ReliableLossyPipeTests.cs`（既有故障注入参考）
- `tests/Skynet.Core.Tests/TcpTransportTests.cs`
- `src/Skynet.Cluster/FaultInjectingStream.cs`
- `src/Skynet.Cluster/TcpTransport.cs`（StreamDecorator hook）
- `tests/Skynet.Core.Tests/FaultInjectingStreamTests.cs`
- `tests/Skynet.Core.Tests/TcpTransportFaultInjectionTests.cs`
- `docs/transport.md`

## 开发记录
- 2026-09-15：实现完成，`dotnet build Skynet.sln -c Release` 0 warning / 0 error（TreatWarningsAsErrors 下），
  `dotnet test Skynet.sln` 266/266 通过（250 既有 + 16 新增）。

### 注入点选择（任务要求的二选一及理由）
选择了 **a. `FaultInjectingStream`（Stream 装饰 + TcpTransport stream factory hook）**，未选
b. `FaultInjectingTransport`（装饰 ITransport）。理由：
- `ITransport` 只有 `SendAsync` 一个方法，装饰它只能看到**出站 envelope**：既拿不到心跳/握手帧，也拿不到读方向，
  任务要求的**半开（停止读取但不断开）**在 envelope 层根本无法构造；手动断连也只能 dispose 整个 transport（等价于节点下线，而非链路故障）。
- Stream 层装饰可以同时控制读写两个方向、按**帧**做丢包决策、并通过死链检测语义（D-3）触发半开判定，且对 KCP/TCP 的生产代码零语义改动。
- TCP 是字节流，"丢包"必须帧原子：装饰器先把写入字节重组为完整帧（`[type:1][length:4 BE][payload]`）再决策丢弃/延迟，
  保证丢一个帧绝不会撕坏字节流（消息损坏不可能发生）——这是 envelope 级装饰同样做不到的粒度声明。

### 组件设计（`src/Skynet.Cluster/FaultInjectingStream.cs`）
- `FaultInjectionOptions`：`Seed`（唯一随机源）、`DropRate`（帧级丢包率 [0,1]）、`MinWriteDelay`/`MaxWriteDelay`
  （均匀分布写延迟，等值即固定延迟）、`FrameSelector`（可选谓词，选择哪些帧受故障影响；被排除的帧直接转发且**不消耗随机数**）、
  `MaxFrameBytes`（重组帧长上限，超限视为流错位 → 断链 fail-fast）。
- 写路径：写入字节进入装配缓冲 → 重组出完整帧 → 按 FrameSelector/丢包率/延迟采样决策 → 进入出站队列；
  单一 pump 任务按 FIFO 释放（释放时刻单调钳制，**帧永不越序**），是 inner stream 的唯一写者。
- 读路径：`StallReads()` 后 `ReadAsync` 挂起，直到 `ResumeReads()` / `BreakConnection()` / Dispose / 取消令牌触发——
  与半开链路上 socket read 永不完成的可观测行为一致；读停滞冻结接收帧时间戳，使本端心跳循环在 `DeadNodeGracePeriod`
  后按 D-3 语义判死断链。
- `BreakConnection()`：唤醒停滞读、后续写抛 `IOException`、dispose inner stream（对端立刻感知断链）；pump 写入失败同样收敛到断链态。
- 计数器 `SelectedFrameCount / DroppedFrameCount / DelayedFrameCount / ForwardedFrameCount` 用于断言"chaos 真的生效了"，
  不依赖时间。同步 `Read` 直通 inner（传输层只用异步 I/O，文档已注明）。

### TcpTransport hook（默认关闭）
- 新增 `TcpTransportOptions.StreamDecorator`（`Func<Stream, Stream>?`，默认 null）：每条连接（入站+出站）各调用一次，
  在 TLS 之后、任何集群帧交换之前生效（装饰的是 TLS 之上的帧流）。null 时行为与 main 完全一致（ApplyStreamDecorator 直接 return）。
- TcpTransport.cs 改动极小：options 属性 + TcpConnection 构造参数与字段 + InitializeAsync 中一行调用 + 两个 new TcpConnection 调用点。
  未触碰帧解析、心跳、pending call、重连等任何生产语义；既有 250 个测试全绿即明文回归证据。

### 测试矩阵（16 个新测试）
组件单测 `FaultInjectingStreamTests`（11，全种子确定性）：
帧重组直通（跨多次 write 的帧重组后逐字节转发）、全丢+FrameSelector 排除帧不消耗随机数、
同种子两次运行转发字节与丢弃数完全一致、延迟 FIFO 保序、读停滞至 Resume、停滞读可被取消、
BreakConnection（读 IOException + 写 IOException + inner 已 dispose）、非法帧长断链、options 校验（丢包率越界 / 延迟下界>上界）。

Chaos 集成 `TcpTransportFaultInjectionTests`（5，真实 loopback 双节点）：
| # | 场景 | 注入 | 断言 |
|---|------|------|------|
| 1 | 25 次连续 CallAsync，20% 丢包（仅 envelope 帧） | caller 侧 seed=20260915 DropRate=0.20 | 每次调用要么内容精确（`echo:msg-i`）要么 1s 超时 `TaskCanceledException`（有界明确错误）；`DroppedFrameCount == 失败数`、`SelectedFrameCount == 25` 精确成立；丢包后连接仍可用（恢复循环成功）。 |
| 2 | 延迟抖动 25 条 fire-and-forget | caller 侧 0–40ms 均匀抖动 | 接收方按序收齐 0..24 且内容无损；`DelayedFrameCount ≥ 25`、零丢失。 |
| 3 | 半开链路 | callee 侧 `StallReads()`（心跳 50ms / 宽限 300ms，对齐 D-3） | 挂起的 CallAsync 在 10s 有界内收到 `RemoteConnectionClosedException(NodeId=node1)`（死链检测判定）；caller 连接表清空后，新调用在全新连接上成功。 |
| 4 | 手动断连 | caller 侧 `BreakConnection()`（in-flight SlowActor 调用） | 挂起调用有界内 `RemoteConnectionClosedException`；连接释放后 echo 调用成功恢复。 |
| 5 | 节点 kill + restart（故障注入下） | caller 侧全程 0–25ms 延迟注入 | 复用 `CallAsync_ShouldReconnectAfterRemoteNodeRestarts` 模式：warm-up → dispose node1 → 连接清理 → 原端口重启 → 调用成功且内容无损；两次连接各自独立注入。 |

任务卡用例 1 的"最终送达（配合 KCP ReliableQueue）"分支：TCP 传输层没有 envelope 级重传（ReliableQueue 不叠在 TCP 上，
见 docs/transport.md），帧被丢弃后唯一的正确语义就是调用方超时失败——测试 1 断言的正是这个"有界明确错误"分支，
"最终送达"分支已由 `ReliableLossyPipeTests`（ReliableQueue over lossy pipe）覆盖，本次未重复建设。

### 确定性手段
- 所有丢包/延迟决策来自 `FaultInjectionOptions.Seed` 的单一 `Random`，无 wall-clock 输入；同种子同帧序列 ⇒ 同故障模式（有单测证明）。
- FrameSelector 排除握手/心跳帧 ⇒ 随机决策数是测试自身流量的纯函数：测试 1 中 `SelectedFrameCount` 恒等于 25、
  `DroppedFrameCount` 恒等于超时次数（跨机器/跨运行可精确断言）。
- 断言只允许"等待上界"（`WaitAsync(10s)`、轮询预算 200×25ms、每调用超时 1–2s），禁止任何"必须在 X 毫秒内完成"式实时断言。
- 每条链路的注入流独立建实例（每连接调用一次工厂），半开/断连只影响被点名的实例，恢复走全新连接。

### 复跑记录（确定性自检）
- 全量 `dotnet test Skynet.sln -c Release`（含新增 16 个测试）连续 3 次全绿（266/266 ×3）；
  另追加 3 次全量压测复跑亦全绿（累计 7 次全量，仅首次出现过一次 Skynet.Net.Tests 偶发失败，
  该套件单独复跑与后续 6 次全量均 96/96 全绿，且本次未触碰 Skynet.Net——判定为并行全量下的既有偶发，与 E-6 无关，不修，仅记录）。
- 新增测试无 [Retry]，无 sleep 时序假设（仅等待上界与轮询），端口动态分配。

### 偏离与遗留
- 任务卡草拟的组件名 `FaultInjectingTransport` 未采用（理由见上），落地为 `FaultInjectingStream`；kcp 侧未加装饰
  （KCP 是数据报通道，非 Stream，envelope 级装饰对 KCP 的增益无法覆盖半开场景，留待后续任务）。
- "按写操作丢弃"未提供：丢弃半个写入会撕裂 TCP 帧流，把丢包语义变成断连语义；已写入 docs 限制说明。
- 进程级 chaos 编排脚本（任务卡可选项）未做。
- 遗留（pre-existing，未修）：Skynet.Net.Tests 在并行全量下存在一次偶发失败（见复跑记录），与本次改动无关。
- 无无关顺手修复（TcpTransport 仅加 hook，diff 最小化）。

## Review 意见
（待填）

## QA 记录
（待填）
