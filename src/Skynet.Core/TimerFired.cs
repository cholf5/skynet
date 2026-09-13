namespace Skynet.Core;

/// <summary>
/// Payload carried by timer due messages routed to an actor's mailbox.
/// The actual callback is attached to the mailbox message itself; this payload only
/// exists for tracing and diagnostics.
/// </summary>
internal sealed record TimerFired(long TimerId);
