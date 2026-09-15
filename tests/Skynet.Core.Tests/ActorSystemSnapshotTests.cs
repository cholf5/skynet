using System.Collections.Concurrent;
using FluentAssertions;
using Skynet.Core;
using Skynet.Core.Persistence;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// Tests for the optional persistence assembly points on <see cref="ActorSystem"/>:
/// store registration, explicit snapshot/restore through the actor mailbox, and the guarantee
/// that actors without a store or without <see cref="ISnapshotable"/> keep their exact
/// persistence-free behavior.
/// </summary>
public sealed class ActorSystemSnapshotTests
{
	[Fact]
	public async Task SnapshotActorAsync_WithoutRegisteredStore_ShouldFailFast()
	{
		await using var system = new ActorSystem();
		var actor = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => system.SnapshotActorAsync(actor.Handle, "svc:counter")).ConfigureAwait(false);
	}

	[Fact]
	public async Task UseSnapshotStore_AfterDispose_ShouldThrow()
	{
		var system = new ActorSystem();
		await system.DisposeAsync().ConfigureAwait(false);

		var act = () => system.UseSnapshotStore(new RecordingSnapshotStore());
		act.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public async Task SnapshotActorAsync_NonSnapshotableActor_ShouldThrowAndLeaveStoreUntouched()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);
		var actor = await system.CreateActorAsync(() => new PlainActor()).ConfigureAwait(false);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => system.SnapshotActorAsync(actor.Handle, "svc:plain")).ConfigureAwait(false);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => system.RestoreActorSnapshotAsync(actor.Handle, "svc:plain")).ConfigureAwait(false);

		// The hook must fail fast before any store I/O: persistence stays a pure opt-in.
		store.SavedSnapshots.Should().BeEmpty();
		store.LoadRequests.Should().BeEmpty();
	}

	[Fact]
	public async Task SnapshotActorAsync_UnknownHandle_ShouldThrowKeyNotFound()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);

		await Assert.ThrowsAsync<KeyNotFoundException>(
			() => system.SnapshotActorAsync(new ActorHandle(4242), "svc:ghost")).ConfigureAwait(false);
		store.SavedSnapshots.Should().BeEmpty();
	}

	[Fact]
	public async Task SnapshotActorAsync_ShouldPassKeyAndGenerationToStore()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);
		var actor = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);

		await system.SnapshotActorAsync(actor.Handle, "svc:counter", 7).ConfigureAwait(false);
		await system.SnapshotActorAsync(actor.Handle, "svc:counter").ConfigureAwait(false);

		store.SavedSnapshots.Should().HaveCount(2);
		store.SavedSnapshots[0].ActorKey.Should().Be("svc:counter");
		store.SavedSnapshots[0].Generation.Should().Be(7);
		// A null generation saves into the unversioned overwrite slot (0).
		store.SavedSnapshots[1].Generation.Should().Be(ActorSnapshotKey.Unversioned);
	}

	[Fact]
	public async Task SnapshotAndRestore_ShouldCarryStateToANewInstanceAtTheCapturedPoint()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);
		var first = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);

		await first.CallAsync<long>(new CounterActor.Increment(3)).ConfigureAwait(false);
		var snapshot = await system.SnapshotActorAsync(first.Handle, "svc:counter", 1).ConfigureAwait(false);
		snapshot.Payload.Should().NotBeEmpty();

		// The first instance keeps running and diverges from the captured point...
		await first.CallAsync<long>(new CounterActor.Increment(10)).ConfigureAwait(false);
		var liveCount = await first.CallAsync<long>(new CounterActor.GetCount()).ConfigureAwait(false);
		liveCount.Should().Be(13);

		// ...while a second instance resumes exactly from the snapshot, not from the live state.
		var second = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);
		await system.RestoreActorSnapshotAsync(second.Handle, "svc:counter", 1).ConfigureAwait(false);
		var restored = await second.CallAsync<long>(new CounterActor.GetCount()).ConfigureAwait(false);
		restored.Should().Be(3, "the new instance must observe the state as captured, not the live actor's state");

		// The restored instance continues the state stream from the restored point.
		var next = await second.CallAsync<long>(new CounterActor.Increment(1)).ConfigureAwait(false);
		next.Should().Be(4);
	}

	[Fact]
	public async Task RestoreActorSnapshotAsync_WithoutStoredSnapshot_ShouldReturnNullAndLeaveActorUntouched()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);
		var actor = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);

		var result = await system.RestoreActorSnapshotAsync(actor.Handle, "svc:missing").ConfigureAwait(false);

		result.Should().BeNull();
		store.LoadRequests.Should().ContainSingle(key => key.ActorKey == "svc:missing" && key.Generation == null);
		var count = await actor.CallAsync<long>(new CounterActor.GetCount()).ConfigureAwait(false);
		count.Should().Be(0, "a failed lookup must not perturb the actor");
	}

	[Fact]
	public async Task SnapshotActorAsync_KilledActor_ShouldNotHang()
	{
		var store = new RecordingSnapshotStore();
		await using var system = new ActorSystem();
		system.UseSnapshotStore(store);
		var actor = await system.CreateActorAsync(() => new CounterActor()).ConfigureAwait(false);
		(await system.KillAsync(actor.Handle).ConfigureAwait(false)).Should().BeTrue();

		// The mailbox is completed after a kill, so the enqueue itself fails instead of waiting
		// for a callback that would never run.
		await Assert.ThrowsAnyAsync<Exception>(
			() => system.SnapshotActorAsync(actor.Handle, "svc:counter")).ConfigureAwait(false);
	}

	/// <summary>
	/// In-memory fake used to observe exactly what the framework hands to the store without
	/// involving file I/O.
	/// </summary>
	private sealed class RecordingSnapshotStore : IActorSnapshotStore
	{
		private readonly object _sync = new();
		private readonly Dictionary<string, SortedDictionary<long, ActorSnapshot>> _backing = new(StringComparer.Ordinal);

		public List<ActorSnapshot> SavedSnapshots { get; } = new();

		public List<ActorSnapshotKey> LoadRequests { get; } = new();

		public Task SaveAsync(ActorSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			lock (_sync)
			{
				SavedSnapshots.Add(snapshot);
				if (!_backing.TryGetValue(snapshot.ActorKey, out var generations))
				{
					generations = [];
					_backing[snapshot.ActorKey] = generations;
				}

				generations[snapshot.Generation] = snapshot;
			}

			return Task.CompletedTask;
		}

		public Task<ActorSnapshot?> LoadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default)
		{
			lock (_sync)
			{
				LoadRequests.Add(key);
				if (!_backing.TryGetValue(key.ActorKey, out var generations) || generations.Count == 0)
				{
					return Task.FromResult<ActorSnapshot?>(null);
				}

				if (key.Generation.HasValue)
				{
					return Task.FromResult(
						generations.TryGetValue(key.Generation.Value, out var snapshot) ? snapshot : null);
				}

				// Null generation: latest available generation of the actor.
				return Task.FromResult<ActorSnapshot?>(generations.Values.Last());
			}
		}
	}

	/// <summary>
	/// Stateful actor that opts into persistence via <see cref="ISnapshotable"/>. The snapshot
	/// payload is deliberately hand-rolled to underline that the payload format belongs entirely
	/// to the actor.
	/// </summary>
	private sealed class CounterActor : Actor, ISnapshotable
	{
		private long _count;

		public readonly record struct Increment(long Delta);

		public readonly record struct GetCount;

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case Increment request:
					_count += request.Delta;
					return Task.FromResult<object?>(_count);
				case GetCount:
					return Task.FromResult<object?>(_count);
				default:
					throw new InvalidOperationException($"Unsupported payload {envelope.Payload?.GetType().Name}.");
			}
		}

		public Task<byte[]> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
		{
			return Task.FromResult(BitConverter.GetBytes(_count));
		}

		public Task RestoreSnapshotAsync(byte[] payload, CancellationToken cancellationToken = default)
		{
			_count = BitConverter.ToInt64(payload, 0);
			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Actor without any persistence awareness: the hooks must never reach it.
	/// </summary>
	private sealed class PlainActor : Actor
	{
		private readonly ConcurrentQueue<object> _received = new();

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			_received.Enqueue(envelope.Payload);
			return Task.FromResult<object?>(null);
		}
	}
}
