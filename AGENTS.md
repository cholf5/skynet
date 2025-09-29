
阅读 ./docs/PRD.md 了解项目背景和需求。

# 看板工作流

本项目使用看板工作流进行任务管理，请严格遵守以下规范。

统一 Kanban 看板列（项目级）：

* **Backlog**（需求池）
* **To Do**（Sprint/当前周期待办）
* **In Progress**（开发中）
* **In Review**（代码审查）
* **Testing / QA**（QA 测试中）
* **Blocked**（阻塞）
* **Done**（验收通过）

> 每个任务卡必须包含：目标、子任务、开发者、估算复杂度（S/M/L）、验收标准、测试用例、相关文件/设计稿、依赖任务。

## 任务状态定义（严格）

* **开发（In Progress）**：除了完成任务外，还需包含单元测试（若适用）。
* **Code Review（In Review）**：PR 描述需写清修改点、运行方式、影响面、回归风险；至少 1 名审查者通过（由你切换身份扮演）。
* **QA（Testing）**：QA 按测试用例执行，记录 BUG，BUG 分级（P0/P1/P2）。所有 P0 必须解决，P1 需评估。QA 完成后 QA 在卡片上写“QA Passed”（由你切换身份扮演）。
* **验收/Done**：产品经理或维护者对接收准则（Acceptance Criteria）进行最终验收，若通过，移动卡片到 Done。（由你切换身份扮演）

Kanban 数据记录在 `./kanban/` 目录（Markdown 格式）.
- `./kanban/board.md` 维护看板状态。
- `./kanban/issues/` 目录存放每个任务的详细描述（Markdown）, 每个任务一个文件，文件名为任务 ID，例如 `A-1-initialize-repo.md`。此文件内不但要记录任务描述，还要记录开发过程中遇到的问题与解决方案、BUG 记录、Review 意见等。

---

请检查 ./kanban/ 目录：
- 如果不存在，请根据 PRD 创建初始看板和任务文件。
- 如果存在，请根据看板上的任务状态，继续推进任务，或遵照用户本次的指示进行工作。

## 编码规范
👌 明白，这里我帮你整理一份 **编码规范与约定**，以及 **TDD 是否适用的评估**。这些内容是专门写给编程 Agent 的（即自动化开发工具 / AI 编码助手），以确保整个项目风格统一、可维护、可扩展。

---

# Skynet 编码规范与约定

## 1. 项目结构

* **解决方案结构**

  ```
  Skynet.sln
  src/
    Skynet.Core/        // 核心运行时 (ActorSystem, ActorRef, send/call)
    Skynet.Cluster/     // 跨节点通信
    Skynet.Net/         // Socket/Http/WebSocket/Gate 模块
    Skynet.Extras/      // Multicast, DataCenter, DebugConsole 等
    Skynet.Examples/    // 示例项目 (EchoServer, ChatRoom, ClusterDemo)
  tests/
    Skynet.Core.Tests/
    Skynet.Cluster.Tests/
    ...
  ```
* **命名空间**

  * `Skynet.Core`
  * `Skynet.Cluster`
  * `Skynet.Net`
  * `Skynet.Extras`

---

## 2. C# 编码规范

* **语言版本**：C# 13 (net9.0)
* **缩进**：Tab（宽度 4）
* **文件编码**：UTF-8 (no BOM)
* **大括号**：Allman 风格（{ 独占一行）
* **类命名**：PascalCase（`ActorSystem`, `ActorRef`）
* **方法命名**：PascalCase（`Send`, `CallAsync`）
* **变量命名**：camelCase（`actorRef`, `channel`）
* **字段命名**：前缀 `_`（`_mailbox`, `_logger`）
* **常量**：大写 + 下划线（`MAX_QUEUE_LENGTH`）
* **接口**：以 `I` 开头（`IActor`, `ILoginService`）
* **泛型参数**：以 `T` 开头（`TMessage`, `TResponse`）
* 禁止 var 滥用，只有在类型显而易见时才允许 var
* 使用表达式主体 (=>) 时必须保持简洁，不要写复杂逻辑

---

## 3. 异步与并发

* **所有异步方法必须返回 `Task` 或 `Task<T>`**
* 严禁使用 `async void`，除非是事件处理器
* 优先使用 `System.Threading.Channels` 实现 mailbox
* Actor 内部保证单线程，不允许直接访问外部线程共享状态

---

## 4. Actor 模型约定

1. **ActorRef**：唯一标识符（handle），不直接暴露内部对象引用
2. **消息处理**：严格顺序，基于 `Channel` 队列
3. **服务注册**：

   * `ActorSystem.Register("login", new LoginActor());`
   * 支持 `GetByName` 和 `GetByHandle`
4. **远程调用**：通过 Cluster 模块透明转发
5. **错误处理**：消息处理异常要 catch 并写入日志，不能导致 Actor 崩溃

---

## 5. 日志与监控

* 使用 `Microsoft.Extensions.Logging`
* 日志等级：

  * `Debug`：调试信息
  * `Info`：正常运行事件
  * `Warn`：潜在风险
  * `Error`：消息处理失败
* 监控指标：

  * Actor 队列长度
  * 平均消息处理耗时
  * 错误次数

---

## 6. 序列化与 RPC

* 默认使用 **MessagePack-CSharp**
* RPC 调用约定：

  * 使用接口定义（`ILoginActor`）
  * 通过 Source Generator 自动生成消息封装/解封装
  * 禁止使用反射序列化

---

## 7. Cluster 模块约定

* 节点间通信基于 TCP + MessagePack
* Cluster 注册表：`nodeName -> endpoint`
* 跨节点调用与本地一致：`Send` / `CallAsync`
* 用户不必关心远程/本地差异

---

## 8. 测试规范

* 使用 `xUnit` + `FluentAssertions`
* 单元测试文件命名：`ClassNameTests.cs`
* 每个核心模块必须有 ≥ 80% 覆盖率
* 示例：

  ```csharp
  [Fact]
  public async Task Actor_Should_Process_Message_In_Order() {
      var actor = new TestActor();
      var system = new ActorSystem();
      var handle = system.Register("test", actor);

      await system.Send(handle, new TestMessage("hello"));
      actor.ProcessedMessages.Should().ContainInOrder("hello");
  }
  ```

---

# 是否使用 TDD 的评估

## 优点

* Actor 模型的并发语义、消息顺序、异常处理，非常容易出 subtle bug
* 提前写测试能确保：

  * 消息顺序正确性
  * `send` 和 `call` 语义一致
  * Cluster 跨节点消息投递可靠
* 有助于避免回归 bug（未来扩展 socket/http/cluster 时，核心不会被破坏）

## 缺点

* 初期开发速度会慢一些
* 对 AI 编码 Agent 来说，TDD 要求“先写测试再写实现”，可能会导致生成逻辑碎片化

## 建议

* 不需要 **纯 TDD**（严格红/绿/重构循环），那样效率太低
* 采用 **Hybrid 模式**：

  * 编程 Agent 先写核心实现 → 再立即写对应的单元测试
  * 对 ActorSystem / Cluster / Network 这类核心模块，必须有覆盖率 > 80%
  * 对 Demo/Example 服务，测试可选

👉 总结：**不采用严格 TDD，但测试必须同步开发，尤其是核心并发语义的单元测试。**

# Skynet 测试用例清单（首批必须覆盖）

## 1. Actor 基础语义

* **消息顺序性**

  * 连续发送 100 条消息，验证 Actor 按顺序处理
  * 不允许出现乱序

* **Send 与 Call 区别**

  * `Send`：单向消息，调用方无需等待返回
  * `CallAsync`：阻塞等待返回值，Actor 正确返回结果
  * 多次 `CallAsync` 并发时，结果应一一对应

* **异常处理**

  * Actor 内抛异常时：

    * 不会导致 Actor 崩溃
    * 错误被记录日志
    * 其他消息仍能继续处理

---

## 2. 服务注册与查找

* **Handle 注册**

  * 注册 Actor → 返回唯一 handle
  * handle 不重复

* **Name 注册**

  * `Register("login", actor)` 成功后
  * `GetByName("login")` 返回正确 handle

* **唯一服务**

  * `UniqueService("datacenter")` 多次调用，始终返回同一个 Actor

---

## 3. 并发与隔离性

* **独立 Actor 隔离**

  * 两个不同 Actor 各自处理消息，互不干扰
  * 一个 Actor 队列满时，不影响其他 Actor

* **多线程安全**

  * 并发 `Send` 消息，不丢失、不重复

---

## 4. Cluster 跨节点

（可先用模拟网络实现，后续接入真实 TCP）

* **本地 vs 远程透明性**

  * `Send(localActor)` 与 `Send(remoteActor)` 效果一致
  * `CallAsync(localActor)` 与 `CallAsync(remoteActor)` 效果一致

* **网络异常处理**

  * 节点断开连接时，`CallAsync` 超时/返回错误
  * 重新连接后可恢复

---

## 5. 网络模块

* **Socket Server**

  * 能接收客户端连接
  * 正确收发消息

* **GateServer**

  * 一个客户端 → 绑定到一个 Session Actor
  * 断线重连时，旧 Session 销毁，新 Session 建立

* **Http Server**

  * GET / POST 正常返回
  * 异常请求返回 400/500

---

## 6. 工具服务

* **DataCenter**

  * 设置 / 获取值
  * 跨服务访问一致性

* **Multicast**

  * 多个 Actor 订阅同一个主题
  * 发布消息时，所有订阅者都能收到

* **DebugConsole**

  * 输入命令 `list`，输出当前 Actor 列表
  * 输入命令 `stat`，输出消息处理统计

---

## 7. 性能保障

（可作为基准测试）

* **消息吞吐量**

  * 单 Actor 每秒能处理 ≥ 10 万条小消息
* **延迟**

  * 单次 `CallAsync` RTT < 1ms（本地）
* **内存泄漏**

  * 连续 1 小时消息压测，内存不增长异常

---

# 测试框架与要求

* 测试框架：`xUnit` + `FluentAssertions`
* Mock 工具：`Moq` 或自己实现 Fake Actor
* 覆盖率：核心模块 ≥ 80%
* 每个功能模块（Core/Cluster/Net/Extras）必须有独立测试项目

---

👉 这份清单的目标：

* **保证 Actor 语义绝对正确**（顺序性、隔离性、异常处理）
* **保证 Cluster 跨节点语义正确**（远程与本地一致）
* **保证基础网络和工具服务可用**
