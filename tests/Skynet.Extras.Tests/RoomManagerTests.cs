using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Skynet.Core;
using Skynet.Extras;
using Skynet.Net;
using Xunit;

namespace Skynet.Extras.Tests;

public sealed class RoomManagerTests
{
	[Fact]
	public async Task BroadcastAsyncDeliversPayloadToAllMembers()
	{
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var firstInbox = new ConcurrentQueue<string>();
		var secondInbox = new ConcurrentQueue<string>();
		var firstActor = new RecordingSessionActor(firstInbox);
		var secondActor = new RecordingSessionActor(secondInbox);
		var first = await system.CreateActorAsync(() => firstActor).ConfigureAwait(false);
		var second = await system.CreateActorAsync(() => secondActor).ConfigureAwait(false);
		var metadataA = new SessionMetadata("session-a", "test", null, DateTimeOffset.UtcNow);
		var metadataB = new SessionMetadata("session-b", "test", null, DateTimeOffset.UtcNow);
		manager.Join("lobby", new RoomParticipant(first.Handle, metadataA));
		manager.Join("lobby", new RoomParticipant(second.Handle, metadataB));

		var result = await manager.BroadcastTextAsync("lobby", "hello world").ConfigureAwait(false);

		result.Attempted.Should().Be(2);
		result.Enqueued.Should().Be(2);

		// BroadcastAsync only guarantees the payloads were enqueued; wait for both actors
		// to actually process them before asserting on the inboxes.
		await Task.WhenAll(firstActor.MessageProcessed, secondActor.MessageProcessed)
			.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

		firstInbox.Should().ContainSingle(msg => msg.Contains("hello world"));
		secondInbox.Should().ContainSingle(msg => msg.Contains("hello world"));
	}

	[Fact]
	public async Task LeaveRemovesMemberAndRoom()
	{
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var inbox = new ConcurrentQueue<string>();
		var actor = await system.CreateActorAsync(() => new RecordingSessionActor(inbox)).ConfigureAwait(false);
		var metadata = new SessionMetadata("session-x", "test", null, DateTimeOffset.UtcNow);
		manager.Join("solo", new RoomParticipant(actor.Handle, metadata));

		var leave = manager.Leave("solo", actor.Handle);

		leave.Member.Should().NotBeNull();
		leave.RoomEmpty.Should().BeTrue();
		manager.GetMembers("solo").Should().BeEmpty();
	}

	[Fact]
	public async Task RemoveSessionCleansUpAllRooms()
	{
		await using var system = new ActorSystem();
		var manager = new RoomManager(system);
		var inbox = new ConcurrentQueue<string>();
		var actor = await system.CreateActorAsync(() => new RecordingSessionActor(inbox)).ConfigureAwait(false);
		var metadata = new SessionMetadata("session-y", "test", null, DateTimeOffset.UtcNow);
		manager.Join("alpha", new RoomParticipant(actor.Handle, metadata));
		manager.Join("beta", new RoomParticipant(actor.Handle, metadata));

		var removed = manager.RemoveSession(actor.Handle);

		removed.Should().Be(2);
		manager.GetMembers("alpha").Should().BeEmpty();
		manager.GetMembers("beta").Should().BeEmpty();
	}

	private sealed class RecordingSessionActor : Actor
	{
		private readonly ConcurrentQueue<string> _inbox;
		private readonly TaskCompletionSource<object?> _messageProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public RecordingSessionActor(ConcurrentQueue<string> inbox)
		{
			_inbox = inbox;
		}

		/// <summary>
		/// Completes once the actor has processed its first outbound message.
		/// </summary>
		public Task MessageProcessed => _messageProcessed.Task;

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			if (envelope.Payload is SessionOutboundMessage outbound)
			{
				var text = Encoding.UTF8.GetString(outbound.Payload.Span);
				_inbox.Enqueue(text);
				_messageProcessed.TrySetResult(null);
			}

			return Task.FromResult<object?>(null);
		}
	}
}
