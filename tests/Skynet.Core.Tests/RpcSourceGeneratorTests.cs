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

		var ping = proxy.Ping("guest");
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

	[SkynetActor("login", Unique = true)]
	public interface ILoginActor
	{
		Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
		ValueTask NotifyAsync(LoginNotice notice);
		string Ping(string name);
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

		public string Ping(string name)
		{
			return $"PONG: {name}";
		}
	}

	[MessagePackObject]
	public sealed record LoginRequest([property: Key(0)] string Username, [property: Key(1)] string Password);

	[MessagePackObject]
	public sealed record LoginResponse([property: Key(0)] bool Success, [property: Key(1)] string WelcomeMessage);

	[MessagePackObject]
	public sealed record LoginNotice([property: Key(0)] string Username, [property: Key(1)] string Message);
}
