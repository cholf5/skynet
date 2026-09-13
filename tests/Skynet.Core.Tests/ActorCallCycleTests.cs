using System;
using System.Threading.Tasks;
using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// D-4：Actor 调用环检测。mailbox 严格串行，环状调用（A call B call A）会永久死锁；
/// 框架必须在环形成的瞬间响亮报错，而不是让调用悬挂。
/// </summary>
public sealed class ActorCallCycleTests
{
	[Fact]
	public async Task Call_ShouldFailFastWithCyclePath_WhenCallGraphContainsCycle()
	{
		await using var system = new ActorSystem();
		var a = await system.CreateActorAsync(() => new CallRelayActor(), "a");
		var b = await system.CreateActorAsync(() => new CallRelayActor(), "b");

		// 调用图 a → b → a：修复前该调用会永久悬挂（两个串行 mailbox 互等）。
		// 环检测在 b 发起对 a 的 call 时立即抛出，因此本测试快速失败而非超时。
		var exception = await Assert.ThrowsAsync<ActorCallCycleException>(
			() => a.CallAsync<object?>(new Route(b.Handle, new Route(a.Handle, new Done()))));

		exception.Message.Should().Contain("Actor call cycle detected:");
		exception.Message.Should().Contain(" → ");
		exception.Message.Should().Contain(a.Handle.Value.ToString());
		exception.Message.Should().Contain(b.Handle.Value.ToString());

		// 环报错后 a 仍能继续处理后续消息（异常不摧毁 actor）。
		var after = await a.CallAsync<object?>(new Done());
		after.Should().Be("done");
	}

	[Fact]
	public async Task Call_ShouldAllowAcyclicDeepChains()
	{
		await using var system = new ActorSystem();
		var a = await system.CreateActorAsync(() => new CallRelayActor());
		var b = await system.CreateActorAsync(() => new CallRelayActor());
		var c = await system.CreateActorAsync(() => new CallRelayActor());

		// 非环深链 a → b → c 必须正常完成。
		var result = await a.CallAsync<object?>(new Route(b.Handle, new Route(c.Handle, new Done())));
		result.Should().Be("done");
	}

	[Fact]
	public async Task Call_ShouldFailFast_WhenActorCallsItself()
	{
		await using var system = new ActorSystem();
		var a = await system.CreateActorAsync(() => new CallRelayActor());

		var exception = await Assert.ThrowsAsync<ActorCallCycleException>(
			() => a.CallAsync<object?>(new Route(a.Handle, new Done())));

		exception.Message.Should().Contain($"{a.Handle.Value} → {a.Handle.Value}");
	}

	[Fact]
	public async Task Call_ShouldKeepConcurrentIndependentChainsIsolated()
	{
		await using var system = new ActorSystem();
		var x = await system.CreateActorAsync(() => new CallRelayActor());
		var y = await system.CreateActorAsync(() => new CallRelayActor());
		var z = await system.CreateActorAsync(() => new CallRelayActor());
		var p = await system.CreateActorAsync(() => new CallRelayActor());
		var q = await system.CreateActorAsync(() => new CallRelayActor());
		var r = await system.CreateActorAsync(() => new CallRelayActor());

		// 两条并发的独立调用链：任何 AsyncLocal 串扰都会表现为误报环或丢结果。
		var first = x.CallAsync<object?>(new Route(y.Handle, new Route(z.Handle, new Done())));
		var second = p.CallAsync<object?>(new Route(q.Handle, new Route(r.Handle, new Done())));

		var results = await Task.WhenAll(first, second);
		results.Should().Equal("done", "done");
	}

	[Fact]
	public async Task Send_ShouldBreakTheCallChain()
	{
		// Send 是 fire-and-forget，不创建阻塞边，因此绝不进链：A 先 send 给 B（A 的 handler
		// 随即返回），B 收到后再 call A 必须成功。若 send 错误地传播调用链，此测试会误报环。
		await using var system = new ActorSystem();
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var a = await system.CreateActorAsync(() => new SendRelayActor(completion), "a");
		var b = await system.CreateActorAsync(() => new CallbackActor(a.Handle), "b");

		await a.SendAsync(new Route(b.Handle, new Ping()));
		await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

		// a 收到 B 的回 call 并完成信号，说明 B → a 的 call 未被误判为环。
		completion.Task.IsCompleted.Should().BeTrue();
	}

	/// <summary>收到 <see cref="Route"/> 时向目标发起嵌套 call；收到 <see cref="Done"/> 时返回 "done"。</summary>
	private sealed class CallRelayActor : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case Route route:
					return await System.CallAsync<object?>(route.Target, route.Payload, cancellationToken: cancellationToken)
						.ConfigureAwait(false);
				case Done:
					return "done";
				default:
					throw new InvalidOperationException($"Unsupported payload type {envelope.Payload?.GetType().Name}.");
			}
		}
	}

	/// <summary>收到 <see cref="Route"/> 时向目标 fire-and-forget 一条 <see cref="Ping"/>。</summary>
	private sealed class SendRelayActor(TaskCompletionSource completion) : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case Route route:
					await System.SendAsync(route.Target, new Ping(), cancellationToken: cancellationToken).ConfigureAwait(false);
					return null;
				case Done:
					completion.TrySetResult();
					return null;
				default:
					throw new InvalidOperationException($"Unsupported payload type {envelope.Payload?.GetType().Name}.");
			}
		}
	}

	/// <summary>收到 <see cref="Ping"/> 时向目标 call 一条 <see cref="Done"/>。</summary>
	private sealed class CallbackActor(ActorHandle target) : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			if (envelope.Payload is Ping)
			{
				return await System.CallAsync<object?>(target, new Done(), cancellationToken: cancellationToken)
					.ConfigureAwait(false);
			}

			throw new InvalidOperationException($"Unsupported payload type {envelope.Payload?.GetType().Name}.");
		}
	}

	private sealed record Route(ActorHandle Target, object Payload);

	private sealed record Ping;

	private sealed record Done;
}
