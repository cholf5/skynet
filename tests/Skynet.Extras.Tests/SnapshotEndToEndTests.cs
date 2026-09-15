using System.Collections.Concurrent;
using FluentAssertions;
using MessagePack;
using Skynet.Core;
using Skynet.Core.Persistence;
using Skynet.Extras;
using Xunit;

namespace Skynet.Extras.Tests;

/// <summary>
/// End-to-end demonstration of the persistence hooks: a stateful counter actor is snapshotted
/// with <see cref="FileSnapshotStore"/>, its host system is fully torn down (simulating a
/// restart), and a fresh actor in a new system resumes from the persisted snapshot.
/// This is the sample actor + flow documented in docs/persistence.md.
/// </summary>
public sealed class SnapshotEndToEndTests : IDisposable
{
	private readonly string _root;

	public SnapshotEndToEndTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "skynet-e2e-" + Guid.NewGuid().ToString("N"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public async Task Save_NewInstance_Restore_ShouldResumeStateAcrossSystemRestart()
	{
		const string actorKey = "game:scoreboard";

		// --- Run 1: build up state, then explicitly checkpoint it. ---
		{
			await using var system = new ActorSystem();
			system.UseSnapshotStore(new FileSnapshotStore(_root));
			var scoreboard = await system.CreateActorAsync(() => new ScoreboardActor(), "scoreboard")
				.ConfigureAwait(false);

			await scoreboard.CallAsync<long>(new ScoreboardActor.Award("alice", 10)).ConfigureAwait(false);
			await scoreboard.CallAsync<long>(new ScoreboardActor.Award("bob", 5)).ConfigureAwait(false);
			await scoreboard.CallAsync<long>(new ScoreboardActor.Award("alice", 2)).ConfigureAwait(false);

			var snapshot = await system.SnapshotActorAsync(scoreboard.Handle, actorKey).ConfigureAwait(false);
			snapshot.ActorKey.Should().Be(actorKey);
			snapshot.Generation.Should().Be(ActorSnapshotKey.Unversioned);

			// State after the checkpoint must not leak into the persisted snapshot.
			await scoreboard.CallAsync<long>(new ScoreboardActor.Award("alice", 100)).ConfigureAwait(false);
		} // Disposing the system kills every actor: the "restart" boundary.

		// --- Run 2: a brand new system + instance resumes from the file store. ---
		await using var restartedSystem = new ActorSystem();
		restartedSystem.UseSnapshotStore(new FileSnapshotStore(_root));
		var resumed = await restartedSystem.CreateActorAsync(() => new ScoreboardActor(), "scoreboard")
			.ConfigureAwait(false);
		// Note: handles are only unique per system (both systems allocate value 1 here);
		// the stable identity across the restart is the actorKey, not the handle.
		var restored = await restartedSystem.RestoreActorSnapshotAsync(resumed.Handle, actorKey).ConfigureAwait(false);
		restored.Should().NotBeNull();

		var aliceTotal = await resumed.CallAsync<long>(new ScoreboardActor.GetScore("alice")).ConfigureAwait(false);
		aliceTotal.Should().Be(12, "state resumes from the checkpoint, not from after it");

		// The resumed actor continues its state stream on top of the restored data.
		await resumed.CallAsync<long>(new ScoreboardActor.Award("bob", 3)).ConfigureAwait(false);
		var bobTotal = await resumed.CallAsync<long>(new ScoreboardActor.GetScore("bob")).ConfigureAwait(false);
		bobTotal.Should().Be(8);
	}

	[Fact]
	public async Task Restore_WithNoSnapshotOnDisk_ShouldLeaveFreshInstanceEmpty()
	{
		await using var system = new ActorSystem();
		system.UseSnapshotStore(new FileSnapshotStore(_root));
		var scoreboard = await system.CreateActorAsync(() => new ScoreboardActor()).ConfigureAwait(false);

		var restored = await system.RestoreActorSnapshotAsync(scoreboard.Handle, "game:never-saved")
			.ConfigureAwait(false);

		restored.Should().BeNull();
		var total = await scoreboard.CallAsync<long>(new ScoreboardActor.GetScore("anyone")).ConfigureAwait(false);
		total.Should().Be(0);
	}

	[Fact]
	public async Task SnapshotCapture_ShouldBeSerializedWithMessages()
	{
		await using var system = new ActorSystem();
		system.UseSnapshotStore(new FileSnapshotStore(_root));
		var actorInstance = new ScoreboardActor();
		var scoreboard = await system.CreateActorAsync(() => actorInstance).ConfigureAwait(false);
		await scoreboard.CallAsync<long>(new ScoreboardActor.Award("alice", 1)).ConfigureAwait(false);

		// Award and capture are enqueued back to back; the capture must run after the award,
		// on the same loop, so the stored payload already contains the award.
		var awardTask = scoreboard.CallAsync<long>(new ScoreboardActor.Award("bob", 7));
		var snapshotTask = system.SnapshotActorAsync(scoreboard.Handle, "ordering");
		await Task.WhenAll(awardTask, snapshotTask).ConfigureAwait(false);

		var captured = MessagePackSerializer.Deserialize<ScoreboardState>(
			(await snapshotTask.ConfigureAwait(false)).Payload);
		captured.Scores.Should().ContainKey("alice").WhoseValue.Should().Be(1);
		captured.Scores.Should().ContainKey("bob").WhoseValue.Should().Be(7);
		actorInstance.Observations.Should().OnlyHaveUniqueItems("each hook ran exactly once, on the actor loop");
	}

	/// <summary>
	/// The sample stateful actor: a scoreboard keyed by player name. State is a dictionary,
	/// serialized with attributed MessagePack records (the recommended payload format).
	/// </summary>
	private sealed class ScoreboardActor : Actor, ISnapshotable
	{
		private readonly Dictionary<string, long> _scores = new(StringComparer.Ordinal);

		/// <summary>
		/// Records every hook invocation to prove captures/restores run on the message loop,
		/// serialized with regular messages.
		/// </summary>
		public ConcurrentQueue<string> Observations { get; } = new();

		public readonly record struct Award(string Player, long Points);

		public readonly record struct GetScore(string Player);

		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			switch (envelope.Payload)
			{
				case Award award:
					_scores.TryGetValue(award.Player, out var current);
					_scores[award.Player] = current + award.Points;
					return Task.FromResult<object?>(_scores[award.Player]);
				case GetScore query:
					return Task.FromResult<object?>(_scores.GetValueOrDefault(query.Player));
				default:
					throw new InvalidOperationException($"Unsupported payload {envelope.Payload?.GetType().Name}.");
			}
		}

		public Task<byte[]> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
		{
			Observations.Enqueue("capture");
			return Task.FromResult(MessagePackSerializer.Serialize(new ScoreboardState(new Dictionary<string, long>(_scores))));
		}

		public Task RestoreSnapshotAsync(byte[] payload, CancellationToken cancellationToken = default)
		{
			Observations.Enqueue("restore");
			var state = MessagePackSerializer.Deserialize<ScoreboardState>(payload);
			_scores.Clear();
			foreach (var pair in state.Scores)
			{
				_scores[pair.Key] = pair.Value;
			}

			return Task.CompletedTask;
		}
	}
}

/// <summary>
/// Serializable state of <see cref="ScoreboardActor"/>. Attributed MessagePack records are the
/// recommended snapshot payload format: no reflection serialization is involved.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record ScoreboardState([property: Key(0)] Dictionary<string, long> Scores);
