namespace Skynet.Core.Persistence;

/// <summary>
/// Opt-in contract implemented by actors that can persist their state as an opaque snapshot
/// payload. Actors that do not implement this interface are never touched by the persistence
/// hooks — no checks, no allocations, no behavior change.
/// </summary>
/// <remarks>
/// <para>
/// Both members are invoked <em>on the actor's message loop</em> (as mailbox system callbacks), so
/// implementations run serialized with all other message processing and may safely access actor
/// state without additional synchronization. Implementations must not dispatch messages to
/// themselves (the mailbox is busy with this callback) and must keep captures cheap: the message
/// loop is suspended until the hook completes.
/// </para>
/// <para>
/// The payload format is entirely owned by the actor implementation. MessagePack with
/// <c>[MessagePackObject]</c>-attributed state records is the recommended default (no reflection
/// serialization); the framework never inspects the bytes.
/// </para>
/// </remarks>
public interface ISnapshotable
{
	/// <summary>
	/// Captures the actor's current state as a serializable payload. Called on the actor's
	/// message loop while no other message is being processed.
	/// </summary>
	/// <param name="cancellationToken">Token that is cancelled when the actor shuts down mid-capture.</param>
	/// <returns>A task producing the snapshot payload handed to <see cref="IActorSnapshotStore.SaveAsync"/>.</returns>
	Task<byte[]> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Reinstates the actor's state from a previously captured payload. Called on the actor's
	/// message loop; messages sent by the caller after
	/// <see cref="ActorSystem.RestoreActorSnapshotAsync"/> returned are guaranteed to observe the
	/// restored state.
	/// </summary>
	/// <param name="payload">The payload produced by an earlier <see cref="CaptureSnapshotAsync"/> call.</param>
	/// <param name="cancellationToken">Token that is cancelled when the actor shuts down mid-restore.</param>
	/// <returns>A task that completes when the state has been reinstated.</returns>
	Task RestoreSnapshotAsync(byte[] payload, CancellationToken cancellationToken = default);
}
