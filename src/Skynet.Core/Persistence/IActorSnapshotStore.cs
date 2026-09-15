namespace Skynet.Core.Persistence;

/// <summary>
/// Plugin point for persisting actor snapshots. Implementations live outside the framework
/// (e.g. <c>Skynet.Extras.FileSnapshotStore</c> for a file-system sample); the framework never
/// activates persistence on its own.
/// </summary>
/// <remarks>
/// <para>
/// A store is registered explicitly via <see cref="ActorSystem.UseSnapshotStore"/>, and snapshots
/// are only taken or restored when the caller invokes <see cref="ActorSystem.SnapshotActorAsync"/>
/// / <see cref="ActorSystem.RestoreActorSnapshotAsync"/>. Actors that never opt in
/// (<see cref="ISnapshotable"/>) and systems without a registered store keep an identical,
/// persistence-free execution path.
/// </para>
/// <para>
/// Implementations must be thread-safe: snapshots of different actors may be saved concurrently,
/// and concurrent saves targeting the same <see cref="ActorSnapshotKey"/> must leave the store
/// with one complete, readable snapshot (last writer wins).
/// </para>
/// </remarks>
public interface IActorSnapshotStore
{
	/// <summary>
	/// Persists a snapshot under the key carried by <paramref name="snapshot"/>. Saving under a
	/// generation that already exists overwrites that generation (last writer wins).
	/// </summary>
	/// <param name="snapshot">The snapshot to persist. <see cref="ActorSnapshot.Generation"/> must be a concrete value (<see cref="ActorSnapshotKey.Unversioned"/> for the overwrite slot, otherwise a positive generation).</param>
	/// <param name="cancellationToken">Token used to cancel the save.</param>
	/// <returns>A task that completes when the snapshot is durably stored.</returns>
	Task SaveAsync(ActorSnapshot snapshot, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads a snapshot previously stored via <see cref="SaveAsync"/>.
	/// </summary>
	/// <param name="key">The slot to load. A <see langword="null"/> <see cref="ActorSnapshotKey.Generation"/> resolves the latest available generation of the actor.</param>
	/// <param name="cancellationToken">Token used to cancel the load.</param>
	/// <returns>The stored snapshot, or <see langword="null"/> when no snapshot exists for the key. A stored-but-unreadable snapshot must surface as a typed error (e.g. <c>SnapshotCorruptedException</c>), never as <see langword="null"/>.</returns>
	Task<ActorSnapshot?> LoadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default);
}
