using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MessagePack;
using Skynet.Core;
using Skynet.Core.Serialization;

namespace Skynet.Core.Tests;

public sealed class RpcSourceGeneratorTests
{
	[Fact]
	public async Task Proxy_ShouldInvokeActorMethods()
	{
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new LoginActor(), "login");

		var proxy = actor.CreateProxy<ILoginActor>();
		var response = await proxy.LoginAsync(new LoginRequest("demo", "pwd"));
		response.Success.Should().BeTrue();
		response.WelcomeMessage.Should().Contain("demo");

		var ping = await proxy.PingAsync("guest");
		ping.Should().Be("PONG: guest");

		await proxy.NotifyAsync(new LoginNotice("demo", "connected"));
	}

	[Fact]
	public async Task System_ShouldReturnNamedServiceProxy()
	{
		await using var system = new ActorSystem();
		await system.CreateActorAsync(() => new LoginActor(), "login");

		var proxy = system.GetService<ILoginActor>("login");
		var response = await proxy.LoginAsync(new LoginRequest("agent", "secret"));
		response.WelcomeMessage.Should().Contain("agent");
	}

	[Fact]
	public void Registry_ShouldExposeGeneratedMetadata()
	{
		var metadata = RpcContractRegistry.GetMetadata<ILoginActor>();
		metadata.ServiceName.Should().Be("login");
		metadata.Unique.Should().BeTrue();
	}

	[Fact]
	public void Envelope_SerializeRoundtrip_ShouldPreservePayload()
	{
		var envelope = new MessageEnvelope(
			42,
			new ActorHandle(1),
			new ActorHandle(2),
			CallType.Call,
			new LoginNotice("demo", "ping"),
			"trace-123",
			DateTimeOffset.UtcNow,
			TimeSpan.FromSeconds(5),
			3);

		var bytes = MessageEnvelopeSerializer.Serialize(envelope);
		var roundtrip = MessageEnvelopeSerializer.Deserialize(bytes);
		roundtrip.Should().BeEquivalentTo(envelope);
	}

	[Fact]
	public void Envelope_Serialize_ShouldWriteStableContractId()
	{
		var envelope = new MessageEnvelope(
			42,
			new ActorHandle(1),
			new ActorHandle(2),
			CallType.Send,
			new LoginNotice("demo", "ping"),
			null,
			DateTimeOffset.UtcNow,
			null,
			MessageEnvelopeSerializer.WireVersion);

		var bytes = MessageEnvelopeSerializer.Serialize(envelope);
		var dto = MessagePackSerializer.Deserialize<SerializedMessageEnvelope>(bytes);

		dto.Version.Should().Be(MessageEnvelopeSerializer.WireVersion);
		dto.PayloadContractId.Should().Be(
			PayloadContractRegistry.ComputeContractId("Skynet.Core.Tests.RpcSourceGeneratorTests+LoginNotice"));
	}

	[Fact]
	public void Deserialize_ShouldThrowForUnknownContractId()
	{
		var dto = new SerializedMessageEnvelope
		{
			MessageId = 1,
			From = 1,
			To = 2,
			CallType = CallType.Send,
		// Use an id that no other test registers so the registry state stays unpolluted.
		PayloadContractId = 555123456,
		Payload = Array.Empty<byte>(),
			TraceId = null,
			Timestamp = DateTimeOffset.UtcNow.UtcTicks,
			TimeToLiveTicks = null,
			Version = MessageEnvelopeSerializer.WireVersion
		};
		var bytes = MessagePackSerializer.Serialize(dto);

		Func<MessageEnvelope> act = () => MessageEnvelopeSerializer.Deserialize(bytes);

		act.Should().Throw<UnknownPayloadContractException>()
			.Where(exception => exception.ContractId == 555123456)
			.WithMessage("*555123456*PayloadContractRegistry.Register<T>*");
	}

	[Fact]
	public void Deserialize_ShouldRejectLegacyVersion1EnvelopeWithClearError()
	{
		// A wire-format version 1 envelope: string payload type name instead of a contract id.
		var legacy = new LegacySerializedMessageEnvelope
		{
			MessageId = 1,
			From = 1,
			To = 2,
			CallType = CallType.Send,
			PayloadType = "Skynet.Core.Tests.RpcSourceGeneratorTests.LoginNotice, Skynet.Core.Tests",
			Payload = new byte[] { 1 },
			TraceId = null,
			Timestamp = DateTimeOffset.UtcNow.UtcTicks,
			TimeToLiveTicks = null,
			Version = 1
		};
		var bytes = MessagePackSerializer.Serialize(legacy);

		Func<MessageEnvelope> act = () => MessageEnvelopeSerializer.Deserialize(bytes);

		act.Should().Throw<NotSupportedException>()
			.WithMessage("*wire protocol versions below 3*no longer supported*wire protocol version 3*");
	}

	[Fact]
	public void GeneratedContracts_ShouldRegisterPayloadContractIdsAtModuleInit()
	{
		var requestId = PayloadContractRegistry.ComputeContractId(
			"Skynet.Core.Tests.__Skynet.ILoginActor_LoginAsync_0Request");
		PayloadContractRegistry.TryResolve(requestId, out var requestType).Should().BeTrue();
		requestType!.FullName.Should().Be("Skynet.Core.Tests.__Skynet.ILoginActor_LoginAsync_0Request");

		var responseId = PayloadContractRegistry.ComputeContractId(
			"Skynet.Core.Tests.RpcSourceGeneratorTests+LoginResponse");
		PayloadContractRegistry.TryResolve(responseId, out var responseType).Should().BeTrue();
		responseType.Should().Be(typeof(LoginResponse));

		var emptyId = PayloadContractRegistry.ComputeContractId("Skynet.Core.RpcMessages.EmptyPayload");
		PayloadContractRegistry.TryResolve(emptyId, out var emptyType).Should().BeTrue();
		emptyType.Should().Be(typeof(global::Skynet.Core.RpcMessages.EmptyPayload));
	}

	[Fact]
	public async Task VoidProxy_ShouldReturnImmediatelyWithoutWaitingForProcessing()
	{
		await using var system = new ActorSystem();
		var slowActor = new SlowActor();
		var actor = await system.CreateActorAsync(() => slowActor, "slow");

		var proxy = actor.CreateProxy<ISlowActor>();
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		proxy.Fire("work");
		var enqueueElapsed = stopwatch.Elapsed;

		// send 语义：proxy 调用应在入队后立即返回，远小于 actor 的处理耗时。
		enqueueElapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(SlowActor.ProcessingMilliseconds / 2));

		// 消息最终仍会被 actor 处理。
		var completed = await Task.WhenAny(slowActor.Completed, Task.Delay(TimeSpan.FromSeconds(5)));
		completed.Should().Be(slowActor.Completed);
	}

	[Fact]
	public async Task VoidProxy_ShouldRaiseHookAndCounterWhenEnqueueFaults()
	{
		// Fire-and-forget send 永不向调用方抛出入队异常：transport 在 system 之下被释放后，
		// 入队以异步失败任务收场，必须经由 SendEnqueueFailed 钩子 + 指标计数暴露，
		// 而不是被生成代码静默吞掉（此前为 `_ = faulted.Exception`）。
		InProcTransport? transport = null;
		await using var system = new ActorSystem(
			transportFactory: sys => transport = new InProcTransport(sys,
				new InProcTransportOptions { ShortCircuitLocalDelivery = false }));
		var slowActor = new SlowActor();
		var actor = await system.CreateActorAsync(() => slowActor, "slow");

		var hooked = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		ActorRef? hookedActor = null;
		system.SendEnqueueFailed += (actorRef, exception) =>
		{
			hookedActor = actorRef;
			hooked.TrySetResult(exception);
		};

		var proxy = actor.CreateProxy<ISlowActor>();
		proxy.Fire("before");

		// 释放 transport（system 仍在运行）：下一次 send 的入队任务将异步失败。
		await transport!.DisposeAsync();
		proxy.Fire("after");

		var observed = await hooked.Task.WaitAsync(TimeSpan.FromSeconds(5));
		observed.Should().BeOfType<ObjectDisposedException>();
		hookedActor.Should().NotBeNull();
		hookedActor!.Handle.Value.Should().Be(actor.Handle.Value);
		system.Metrics.SendEnqueueFailureCount.Should().Be(1);
	}

	[Fact]
	public async Task VoidProxy_ThrowingSendEnqueueFailedHandlerMustNotEscape()
	{
		// 回归防护：OnSendEnqueueFailed 运行在生成代理丢弃的 OnlyOnFaulted 续接里，
		// handler 抛出的异常若逃逸会重新变成未观察任务异常（本钩子要消除的问题），
		// 并且会跳过同一事件的后续 handler。抛异常的 handler 必须被吞掉（仅 Debug 日志），
		// 后续 handler 与指标计数不受影响。
		InProcTransport? transport = null;
		await using var system = new ActorSystem(
			transportFactory: sys => transport = new InProcTransport(sys,
				new InProcTransportOptions { ShortCircuitLocalDelivery = false }));
		var actor = await system.CreateActorAsync(() => new SlowActor(), "throwing-hook-target");

		var throwingHandlerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		system.SendEnqueueFailed += (_, _) =>
		{
			throwingHandlerRan.TrySetResult();
			throw new InvalidOperationException("handler boom");
		};

		var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
		system.SendEnqueueFailed += (_, exception) => observed.TrySetResult(exception);

		var proxy = actor.CreateProxy<ISlowActor>();
		await transport!.DisposeAsync();
		proxy.Fire("after");

		await throwingHandlerRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
		(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeOfType<ObjectDisposedException>();
		system.Metrics.SendEnqueueFailureCount.Should().Be(1);
	}

	[SkynetActor("slow", Unique = true)]
	public interface ISlowActor
	{
		void Fire(string message);
	}

	private sealed class SlowActor : RpcActor<ISlowActor>, ISlowActor
	{
		public const int ProcessingMilliseconds = 1000;
		private readonly TaskCompletionSource _processed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Completed => _processed.Task;

		public void Fire(string message)
		{
			Thread.Sleep(ProcessingMilliseconds);
			_processed.TrySetResult();
		}
	}

	[SkynetActor("login", Unique = true)]
	public interface ILoginActor
	{
		Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
		ValueTask NotifyAsync(LoginNotice notice);
		Task<string> PingAsync(string name);
	}

	private sealed class LoginActor : RpcActor<ILoginActor>, ILoginActor
	{
		private int _notifications;

		public Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
		{
			return Task.FromResult(new LoginResponse(true, $"Welcome {request.Username}!"));
		}

		public ValueTask NotifyAsync(LoginNotice notice)
		{
			Interlocked.Increment(ref _notifications);
			return ValueTask.CompletedTask;
		}

		public Task<string> PingAsync(string name)
		{
			return Task.FromResult($"PONG: {name}");
		}
	}

	[MessagePackObject]
	public sealed record LoginRequest([property: Key(0)] string Username, [property: Key(1)] string Password);

	[MessagePackObject]
	public sealed record LoginResponse([property: Key(0)] bool Success, [property: Key(1)] string WelcomeMessage);

	[MessagePackObject]
	public sealed record LoginNotice([property: Key(0)] string Username, [property: Key(1)] string Message);

	[MessagePackObject(AllowPrivate = true)]
	internal sealed class LegacySerializedMessageEnvelope
	{
		[Key(0)]
		public long MessageId { get; set; }

		[Key(1)]
		public long From { get; set; }

		[Key(2)]
		public long To { get; set; }

		[Key(3)]
		public CallType CallType { get; set; }

		[Key(4)]
		public string? PayloadType { get; set; }

		[Key(5)]
		public byte[]? Payload { get; set; }

		[Key(6)]
		public string? TraceId { get; set; }

		[Key(7)]
		public long Timestamp { get; set; }

		[Key(8)]
		public long? TimeToLiveTicks { get; set; }

		[Key(9)]
		public int Version { get; set; }
	}
}
