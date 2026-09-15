using System.Text;
using FluentAssertions;
using Skynet.Core.Persistence;
using Skynet.Extras;
using Xunit;

namespace Skynet.Extras.Tests;

/// <summary>
/// Tests for the file-backed snapshot store sample: roundtrips (including non-ASCII keys and
/// large payloads), generation handling, typed corruption errors and concurrent-writer atomicity.
/// </summary>
public sealed class FileSnapshotStoreTests : IDisposable
{
	private readonly string _root;

	public FileSnapshotStoreTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "skynet-snapshots-" + Guid.NewGuid().ToString("N"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public async Task SaveThenLoad_ShouldRoundTripSnapshotExactly()
	{
		var store = new FileSnapshotStore(_root);
		var payload = Encoding.UTF8.GetBytes("état initial 状态 — 🎮");
		var created = DateTimeOffset.UtcNow;
		var snapshot = new ActorSnapshot("état de jeu 玩家/1", 3, created, payload);

		await store.SaveAsync(snapshot).ConfigureAwait(false);
		var loaded = await store.LoadAsync(new ActorSnapshotKey("état de jeu 玩家/1", 3)).ConfigureAwait(false);

		loaded.Should().NotBeNull();
		loaded!.ActorKey.Should().Be(snapshot.ActorKey);
		loaded.Generation.Should().Be(3);
		loaded.Payload.Should().Equal(payload);
		(loaded.CreatedAt - created).Should().BeCloseTo(TimeSpan.Zero, precision: TimeSpan.FromSeconds(1));
	}

	[Fact]
	public async Task SaveThenLoad_ShouldRoundTripLargePayload()
	{
		var store = new FileSnapshotStore(_root);
		// A 1 MiB payload with a pattern that survives serialization truncation checks.
		var payload = new byte[1024 * 1024];
		for (var index = 0; index < payload.Length; index++)
		{
			payload[index] = (byte)(index % 251);
		}

		await store.SaveAsync(new ActorSnapshot("big", 1, DateTimeOffset.UtcNow, payload)).ConfigureAwait(false);

		var loaded = await store.LoadAsync(new ActorSnapshotKey("big", 1)).ConfigureAwait(false);
		loaded.Should().NotBeNull();
		loaded!.Payload.Should().HaveCount(payload.Length);
		loaded.Payload.Should().Equal(payload);
	}

	[Fact]
	public async Task LoadAsync_MissingSnapshot_ShouldReturnNull()
	{
		var store = new FileSnapshotStore(_root);

		var missingGeneration = await store.LoadAsync(new ActorSnapshotKey("nobody", 42)).ConfigureAwait(false);
		var missingLatest = await store.LoadAsync(ActorSnapshotKey.Latest("nobody")).ConfigureAwait(false);

		missingGeneration.Should().BeNull();
		missingLatest.Should().BeNull();
	}

	[Fact]
	public async Task SaveAsync_UnversionedSlot_ShouldOverwritePreviousSnapshot()
	{
		var store = new FileSnapshotStore(_root);
		await store.SaveAsync(new ActorSnapshot("slot", ActorSnapshotKey.Unversioned, DateTimeOffset.UtcNow,
			[1, 2, 3])).ConfigureAwait(false);
		await store.SaveAsync(new ActorSnapshot("slot", ActorSnapshotKey.Unversioned, DateTimeOffset.UtcNow,
			[4, 5, 6])).ConfigureAwait(false);

		// The unversioned slot is a single overwrite slot: exactly one file, newest content wins.
		var directoryFiles = Directory.GetFiles(Path.Combine(_root, Uri.EscapeDataString("slot")));
		directoryFiles.Should().HaveCount(1);

		var loaded = await store.LoadAsync(ActorSnapshotKey.UnversionedSlot("slot")).ConfigureAwait(false);
		loaded!.Payload.Should().Equal((byte[])[4, 5, 6]);
	}

	[Fact]
	public async Task LoadAsync_WithoutGeneration_ShouldResolveLatestGeneration()
	{
		var store = new FileSnapshotStore(_root);
		await store.SaveAsync(new ActorSnapshot("versioned", 1, DateTimeOffset.UtcNow, (byte[])[1])).ConfigureAwait(false);
		await store.SaveAsync(new ActorSnapshot("versioned", 2, DateTimeOffset.UtcNow, (byte[])[2])).ConfigureAwait(false);
		await store.SaveAsync(new ActorSnapshot("versioned", 10, DateTimeOffset.UtcNow, (byte[])[10])).ConfigureAwait(false);

		var latest = await store.LoadAsync(ActorSnapshotKey.Latest("versioned")).ConfigureAwait(false);
		var explicitFirst = await store.LoadAsync(new ActorSnapshotKey("versioned", 1)).ConfigureAwait(false);

		latest!.Generation.Should().Be(10);
		latest.Payload.Should().Equal((byte[])[10]);
		explicitFirst!.Payload.Should().Equal((byte[])[1]);
	}

	[Fact]
	public async Task LoadAsync_TruncatedFile_ShouldThrowSnapshotCorruptedException()
	{
		var store = new FileSnapshotStore(_root);
		await store.SaveAsync(new ActorSnapshot("broken", 1, DateTimeOffset.UtcNow,
			Encoding.UTF8.GetBytes("long payload that should not survive truncation"))).ConfigureAwait(false);
		var path = Path.Combine(_root, Uri.EscapeDataString("broken"), $"{1:D19}.snapshot");
		var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
		// Cut into the 11-byte header itself: below the framing there is nothing left to parse.
		await File.WriteAllBytesAsync(path, bytes[..5]).ConfigureAwait(false);

		var act = async () => await store.LoadAsync(new ActorSnapshotKey("broken", 1)).ConfigureAwait(false);

		var thrown = await act.Should().ThrowAsync<SnapshotCorruptedException>().ConfigureAwait(false);
		thrown.Which.Message.Should().Contain("truncated");
	}

	[Fact]
	public async Task LoadAsync_GarbageBytes_ShouldThrowSnapshotCorruptedException()
	{
		var store = new FileSnapshotStore(_root);
		await store.SaveAsync(new ActorSnapshot("garbage", 1, DateTimeOffset.UtcNow, [9, 9, 9])).ConfigureAwait(false);
		var path = Path.Combine(_root, Uri.EscapeDataString("garbage"), $"{1:D19}.snapshot");
		var garbage = new byte[256];
		Random.Shared.NextBytes(garbage);
		await File.WriteAllBytesAsync(path, garbage).ConfigureAwait(false);

		var act = () => store.LoadAsync(new ActorSnapshotKey("garbage", 1));

		var thrown = await act.Should().ThrowAsync<SnapshotCorruptedException>().ConfigureAwait(false);
		thrown.Which.Message.Should().Contain("magic");
	}

	[Fact]
	public async Task LoadAsync_TrailingBytes_ShouldThrowSnapshotCorruptedException()
	{
		var store = new FileSnapshotStore(_root);
		await store.SaveAsync(new ActorSnapshot("padded", 1, DateTimeOffset.UtcNow, [7])).ConfigureAwait(false);
		var path = Path.Combine(_root, Uri.EscapeDataString("padded"), $"{1:D19}.snapshot");
		var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
		await File.WriteAllBytesAsync(path, bytes.Concat((byte[])[0xFF, 0xFF]).ToArray()).ConfigureAwait(false);

		var act = () => store.LoadAsync(new ActorSnapshotKey("padded", 1));

		var thrown = await act.Should().ThrowAsync<SnapshotCorruptedException>().ConfigureAwait(false);
		thrown.Which.Message.Should().Contain("inconsistent length");
	}

	[Fact]
	public async Task ConcurrentSaves_SameActorKey_ShouldAlwaysLeaveACompleteReadableSnapshot()
	{
		var store = new FileSnapshotStore(_root);
		const int writerCount = 12;
		const int writesPerWriter = 10;
		var payloadBodies = new byte[writerCount][];
		for (var writer = 0; writer < writerCount; writer++)
		{
			payloadBodies[writer] = new byte[4096];
			Array.Fill(payloadBodies[writer], (byte)writer);
		}

		var writers = Enumerable.Range(0, writerCount).Select(async writer =>
		{
			for (var iteration = 0; iteration < writesPerWriter; iteration++)
			{
				await store.SaveAsync(new ActorSnapshot("contended", iteration + 1, DateTimeOffset.UtcNow,
					payloadBodies[writer])).ConfigureAwait(false);
			}
		});

		await Task.WhenAll(writers).ConfigureAwait(false);

		// Every save wrote its own generation; each slot must hold exactly one writer's payload,
		// proving no writer ever observed or left a torn file.
		for (var generation = 1; generation <= writesPerWriter; generation++)
		{
			var loaded = await store.LoadAsync(new ActorSnapshotKey("contended", generation)).ConfigureAwait(false);
			loaded.Should().NotBeNull();
			payloadBodies.Should().Contain(
				body => body.SequenceEqual(loaded!.Payload),
				"generation {0} must be a complete snapshot of exactly one writer", generation);
		}

		(await store.LoadAsync(ActorSnapshotKey.Latest("contended")).ConfigureAwait(false))!
			.Generation.Should().Be(writesPerWriter);
	}

	[Fact]
	public void Constructor_EmptyRoot_ShouldThrow()
	{
		var act = () => new FileSnapshotStore("  ");
		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public async Task SaveAsync_NegativeGeneration_ShouldThrow()
	{
		var store = new FileSnapshotStore(_root);

		var act = () => store.SaveAsync(new ActorSnapshot("negative", -1, DateTimeOffset.UtcNow, [1]));

		await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false);
	}
}
