# ConfigureAwait(false) 使用指南

## 为什么在库代码中大量使用 `ConfigureAwait(false)`

在 Skynet 的运行时和网络等库项目中，我们总是假定调用方可能运行在任意同步上下文（UI 线程、ASP.NET 请求上下文、自定义调度器等）上。如果库内部的 `await` 捕获了调用方的同步上下文，就会带来两个问题：

1. **潜在的死锁**：当调用方使用同步阻塞（例如 `Task.Result` 或 `Task.Wait()`）等待库返回结果时，如果库代码在 `await` 之后试图回到原上下文，就可能永远得不到执行机会，导致死锁。【F:docs/configureawait-guidance.md†L6-L11】
2. **额外的调度开销**：即使不会死锁，把延续切换回原上下文也需要调度和线程切换，对于 IO 密集型的 Actor 与网络场景来说会产生可观的性能损耗。【F:docs/configureawait-guidance.md†L11-L13】

因此，Skynet 的库代码遵循 .NET 官方的建议：**库默认使用 `ConfigureAwait(false)`，让延续在调用线程的线程池上下文执行**，既避免死锁，也减少不必要的上下文切换。例如 `ActorSystem`、`SessionActor` 等核心类型都采用了这个约定。【F:src/Skynet.Core/ActorSystem.cs†L121-L316】【F:src/Skynet.Net/SessionActor.cs†L31-L114】

## 示例项目中是否需要关注性能

`Skynet.Examples` 项目展示了“使用方”应该如何编写调用代码。之所以在示例中依然使用 `ConfigureAwait(false)`，是为了：

* 保持和库代码一致的约定，防止开发者在真实项目中复制示例时遗漏这一点。
* 示例同样运行在服务器环境，没有 UI 同步上下文，直接在库之外继续使用 `ConfigureAwait(false)` 可以避免不必要的上下文捕获，也有助于在压力测试（如房间广播基准）中获得稳定结果。【F:docs/configureawait-guidance.md†L17-L23】【F:src/Skynet.Examples/Program.cs†L23-L216】

因此，示例项目既是“使用方”，也是最佳实践的示范，仍然推荐保留 `ConfigureAwait(false)`。

## Orleans 与 `ConfigureAwait(false)` 的区别

Orleans 在运行时层面提供了自己的同步上下文和调度器：

* **在 Grain 内部**：Orleans 要求延续回到 Grain 的调度上下文，以保证单线程执行语义。所以 Orleans 文档会强调在 Grain 方法中不要使用 `ConfigureAwait(false)`，否则延续可能跳出 Orleans 的调度导致状态竞争。【F:docs/configureawait-guidance.md†L27-L32】
* **从外部调用 Grain**：客户端或网关调用 Grain 方法时，Orleans 的生成代码已经在内部处理了同步上下文的问题，返回的 `Task` 不会试图切换回调用方上下文，因而调用者不需要额外写 `ConfigureAwait(false)`。【F:docs/configureawait-guidance.md†L32-L35】

Skynet 的 Actor 与 Orleans 不同：我们不强制延续回到特定上下文，而是通过邮箱保证 Actor 内部的串行化；只要库代码遵循 `ConfigureAwait(false)`，Actor 的消息循环就能在 .NET 线程池上高效运行。【F:docs/configureawait-guidance.md†L35-L38】

## 结论

* **库代码**：一律使用 `ConfigureAwait(false)`，避免死锁与上下文切换。
* **示例和业务代码**：推荐保持一致，尤其在服务器/服务端场景，这几乎没有副作用。
* **需要同步上下文的场景（如 UI、Orleans Grain 内部）**：应遵循相应框架的指南，不要盲目使用 `ConfigureAwait(false)`。
