using MessagePack;

namespace Skynet.Core.Persistence;

/// <summary>
/// Identifies a snapshot slot inside a <see cref="IActorSnapshotStore"/>: a stable, caller-chosen
/// actor key plus an optional generation number.
/// </summary>
/// <remarks>
/// <para>
/// The actor key is <em>not</em> derived from the runtime <see cref="ActorHandle"/>: handles are
/// recycled across actor restarts, while persisted state must survive them. Callers therefore
/// pass a stable domain key (e.g. the actor's registered service name or <c>"player:123"</c>).
/// </para>
/// <para>
/// <see cref="Generation"/> follows the convention that <see cref="Unversioned"/> (<c>0</c>) is a
/// single overwrite slot for callers that do not manage generations, and any positive value is an
/// explicit, immutable generation. A key with a <see langword="null"/> generation means
/// "resolve the latest available generation" and is only meaningful for load operations; stores
/// must never <em>save</em> under a null generation.
/// </para>
/// </remarks>
public readonly record struct ActorSnapshotKey(string ActorKey, long? Generation)
{
	/// <summary>
	/// Generation reserved for the unversioned slot. Every save into the unversioned slot
	/// overwrites the previous snapshot; explicit generations should start at <c>1</c>.
	/// </summary>
	public const long Unversioned = 0;

	/// <summary>
	/// Creates a key that resolves to the latest available generation of the actor.
	/// </summary>
	/// <param name="actorKey">The stable actor key.</param>
	/// <returns>A key without an explicit generation.</returns>
	public static ActorSnapshotKey Latest(string actorKey) => new(actorKey, null);

	/// <summary>
	/// Creates a key that resolves to the unversioned overwrite slot of the actor.
	/// </summary>
	/// <param name="actorKey">The stable actor key.</param>
	/// <returns>A key pinned to <see cref="Unversioned"/>.</returns>
	public static ActorSnapshotKey UnversionedSlot(string actorKey) => new(actorKey, Unversioned);

	/// <inheritdoc />
	public override string ToString()
	{
		return Generation.HasValue
			? $"{ActorKey}@{Generation.Value}"
			: $"{ActorKey}@latest";
	}
}

/// <summary>
/// A point-in-time capture of one actor's state, stored as an opaque payload.
/// </summary>
/// <remarks>
/// The <see cref="Payload"/> bytes are produced and consumed exclusively by the actor itself
/// (see <see cref="ISnapshotable"/>); the framework and stores treat them as an opaque blob.
/// The record itself is MessagePack-attributed so store implementations can persist the envelope
/// (key, generation, creation time and payload) without reflection-based serialization.
/// </remarks>
[MessagePackObject]
public sealed record ActorSnapshot(
	[property: Key(0)] string ActorKey,
	[property: Key(1)] long Generation,
	[property: Key(2)] DateTimeOffset CreatedAt,
	[property: Key(3)] byte[] Payload);
