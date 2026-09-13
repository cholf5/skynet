namespace Skynet.Core;

/// <summary>
/// Thrown when an actor initiates a call that would form a cycle in the actor call graph
/// (for example A calling B calling A, or an actor calling itself). Actor mailboxes are strictly
/// serial: a handler suspends its mailbox until the call returns, so such a cycle would deadlock
/// every actor involved. The framework fails the call loudly with this exception instead of
/// letting it hang.
/// </summary>
/// <remarks>
/// See <c>docs/adr/0001-actor-reentrancy-semantics.md</c> for the reentrancy decision. The
/// exception message contains the full cycle path, e.g. <c>Actor call cycle detected: 1 → 2 → 1</c>.
/// </remarks>
public sealed class ActorCallCycleException(string message) : InvalidOperationException(message);
