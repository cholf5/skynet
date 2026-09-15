namespace Skynet.Core.Persistence;

/// <summary>
/// Optional plugin point for a write-ahead log: an append-only record stream per actor key that
/// complements snapshots (replay the WAL from the last snapshot to recover the tail of a state
/// stream). This is a contract-only extension point; the framework ships no implementation and
/// no runtime wiring — integrating a WAL is entirely up to the application.
/// </summary>
/// <remarks>
/// Implementations should be crash-safe by design (append with flushed writes, sequence numbers
/// that survive restarts) and treat <see cref="WalRecord.Payload"/> bytes as opaque actor-owned
/// data, exactly like <see cref="ISnapshotable"/> payloads.
/// </remarks>
public interface IActorWal
{
	/// <summary>
	/// Appends one record to the log stream of the given actor key and returns its assigned
	/// sequence number. Sequence numbers are monotonically increasing per actor key and must
	/// remain stable across replays so a snapshot can declare "replay from sequence N".
	/// </summary>
	/// <param name="actorKey">The stable actor key the record belongs to.</param>
	/// <param name="record">The opaque record payload.</param>
	/// <param name="cancellationToken">Token used to cancel the append.</param>
	/// <returns>A task producing the sequence number assigned to the appended record.</returns>
	Task<long> AppendAsync(string actorKey, byte[] record, CancellationToken cancellationToken = default);

	/// <summary>
	/// Replays the log stream of the given actor key in sequence order.
	/// </summary>
	/// <param name="actorKey">The stable actor key to replay.</param>
	/// <param name="fromSequence">The inclusive lower-bound sequence to start replaying from; <c>0</c> replays the whole stream.</param>
	/// <param name="cancellationToken">Token used to cancel the replay.</param>
	/// <returns>An async stream of log records in strictly increasing sequence order.</returns>
	IAsyncEnumerable<WalRecord> ReplayAsync(string actorKey, long fromSequence = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// One record of a write-ahead log stream.
/// </summary>
/// <param name="ActorKey">The stable actor key the record belongs to.</param>
/// <param name="Sequence">The monotonically increasing sequence number assigned by the log.</param>
/// <param name="Payload">The opaque actor-owned record payload.</param>
/// <param name="Timestamp">The wall-clock time the record was appended (informational only).</param>
public readonly record struct WalRecord(
	string ActorKey,
	long Sequence,
	byte[] Payload,
	DateTimeOffset Timestamp);
