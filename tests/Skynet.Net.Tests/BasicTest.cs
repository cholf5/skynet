using System.Text;
using FluentAssertions;
using Skynet.Core;
using Skynet.Extras;
using Skynet.Net;
using Xunit;

namespace Skynet.Net.Tests;

public sealed class BasicTest
{
	[Fact]
	public async Task VeryBasicTest()
	{
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var connection = new SimpleConnection();
		var metadata = new SessionMetadata("test-user", "test", null, DateTimeOffset.UtcNow);

		var actor = await system.CreateActorAsync(() => new SessionActor(connection, metadata, ctx => new RoomSessionRouter(manager))).ConfigureAwait(false);

		// Just expect the welcome message
		var welcome = await connection.ReceiveAsync().ConfigureAwait(false);
		welcome.Should().Contain("WELCOME");

		Console.WriteLine("Basic test passed!");
	}

	private sealed class SimpleConnection : ISessionConnection
	{
		private readonly TaskCompletionSource<string> _tcs = new();

		public System.Net.EndPoint? RemoteEndPoint => null;
		public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;

		public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
		{
			var text = Encoding.UTF8.GetString(payload.Span);
			_tcs.SetResult(text);
			return ValueTask.CompletedTask;
		}

		public ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken = default)
		{
			return ValueTask.CompletedTask;
		}

		public void MarkActivity() { }

		public async Task<string> ReceiveAsync()
		{
			return await _tcs.Task.ConfigureAwait(false);
		}

		public ValueTask DisposeAsync()
		{
			return ValueTask.CompletedTask;
		}
	}
}