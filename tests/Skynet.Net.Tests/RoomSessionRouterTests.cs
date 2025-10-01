using System.Net;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using Skynet.Core;
using Skynet.Extras;
using Skynet.Net;
using Xunit;

namespace Skynet.Net.Tests;

public sealed class RoomSessionRouterTests
{
	// [Fact]
	public async Task BroadcastCommandDeliversToAllMembers()
	{
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var connectionA = new InMemorySessionConnection();
		var connectionB = new InMemorySessionConnection();
		var metadataA = new SessionMetadata("user-a", "test", null, DateTimeOffset.UtcNow);
		var metadataB = new SessionMetadata("user-b", "test", null, DateTimeOffset.UtcNow);
		var actorA = await system.CreateActorAsync(() => new SessionActor(connectionA, metadataA, ctx => new RoomSessionRouter(manager))).ConfigureAwait(false);
		var actorB = await system.CreateActorAsync(() => new SessionActor(connectionB, metadataB, ctx => new RoomSessionRouter(manager))).ConfigureAwait(false);

		// User A joins: expects welcome message only (no self-broadcast to avoid deadlock)
		await connectionA.ExpectAsync("WELCOME").ConfigureAwait(false);

		// User B joins: expects welcome message and system message from User A's broadcast
		await connectionB.ExpectAsync("WELCOME").ConfigureAwait(false);
		await connectionB.ExpectAsync("[lobby]").ConfigureAwait(false);
		// User A also receives the broadcast about User B joining
		await connectionA.ExpectAsync("[lobby]").ConfigureAwait(false);

		// User A joins "general" room: expects confirmation and broadcast to User B
		await actorA.SendAsync(new SessionInboundMessage(Encoding.UTF8.GetBytes("join general"))).ConfigureAwait(false);
		await connectionA.ExpectAsync("JOINED general").ConfigureAwait(false);
		// User B receives the broadcast about User A joining general
		await connectionB.ExpectAsync("[general] *").ConfigureAwait(false);

		// User B joins "general" room: expects confirmation and broadcast to User A
		await actorB.SendAsync(new SessionInboundMessage(Encoding.UTF8.GetBytes("join general"))).ConfigureAwait(false);
		await connectionB.ExpectAsync("JOINED general").ConfigureAwait(false);
		// User A receives the broadcast about User B joining general
		await connectionA.ExpectAsync("[general] *").ConfigureAwait(false);

		// User A sends a message: both users should receive it
		await actorA.SendAsync(new SessionInboundMessage(Encoding.UTF8.GetBytes("say general hello everyone"))).ConfigureAwait(false);
		var messageA = await connectionA.ExpectAsync("user-a: hello everyone").ConfigureAwait(false);
		var messageB = await connectionB.ExpectAsync("user-a: hello everyone").ConfigureAwait(false);

		messageA.Should().Contain("[general]");
		messageB.Should().Contain("[general]");
	}

	private sealed class InMemorySessionConnection : ISessionConnection
	{
		private readonly Channel<byte[]> _messages = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
		{
			SingleReader = false,
			SingleWriter = false
		});
		private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

		public EndPoint? RemoteEndPoint => null;

		public DateTimeOffset LastActivity => _lastActivity;

		public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
		{
			_lastActivity = DateTimeOffset.UtcNow;
			var buffer = payload.ToArray();
			return new ValueTask(_messages.Writer.WriteAsync(buffer, cancellationToken).AsTask());
		}

		public ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
		{
			_lastActivity = DateTimeOffset.UtcNow;
			_messages.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}

		public void MarkActivity()
		{
			_lastActivity = DateTimeOffset.UtcNow;
		}

		public async Task<string> ExpectAsync(string contains, CancellationToken cancellationToken = default)
		{
			var buffer = await _messages.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			var text = Encoding.UTF8.GetString(buffer);
			if (!string.IsNullOrEmpty(contains))
			{
				text.Should().Contain(contains);
			}
			return text;
		}

		public ValueTask DisposeAsync()
		{
			_messages.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}
	}
}
