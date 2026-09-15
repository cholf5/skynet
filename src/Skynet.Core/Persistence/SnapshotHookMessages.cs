namespace Skynet.Core.Persistence;

/// <summary>
/// Payload carried by the mailbox message that asks an actor's loop to capture and store a
/// snapshot. The actual work is attached to the mailbox message as a system callback; this
/// payload only exists for tracing and diagnostics (mirrors <see cref="TimerFired"/>).
/// </summary>
internal sealed record SnapshotRequested(ActorHandle Target);

/// <summary>
/// Payload carried by the mailbox message that asks an actor's loop to apply a restored snapshot.
/// </summary>
internal sealed record SnapshotRestoreRequested(ActorHandle Target);
