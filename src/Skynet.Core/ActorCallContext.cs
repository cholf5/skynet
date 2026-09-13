namespace Skynet.Core;

/// <summary>
/// Provides the ambient actor call chain for the current asynchronous flow, mirroring the
/// <see cref="TraceContext"/> pattern. The chain is established by <see cref="ActorHost"/> when a
/// message begins processing and is consulted by <see cref="ActorSystem.CallAsync"/> to detect
/// cyclical calls before they deadlock serial mailboxes.
/// </summary>
internal static class ActorCallContext
{
	private static readonly AsyncLocal<ActorCallChain?> Current = new();

	/// <summary>
	/// Gets the call chain of the current asynchronous flow, or <see langword="null"/> when the
	/// flow is not processing an actor message (e.g. a top-level caller).
	/// </summary>
	internal static ActorCallChain? CurrentChain => Current.Value;

	/// <summary>
	/// Pushes a call chain scope for the lifetime of the returned disposable. On dispose the
	/// previous chain is restored, so the mailbox loop never leaks a chain into subsequent messages.
	/// </summary>
	/// <param name="chain">The chain that describes the current message's causal call stack.</param>
	/// <returns>A disposable scope that restores the previous chain when disposed.</returns>
	internal static IDisposable BeginScope(ActorCallChain chain)
	{
		var previous = Current.Value;
		Current.Value = chain;
		return new Scope(previous);
	}

	private sealed class Scope(ActorCallChain? previous) : IDisposable
	{
		public void Dispose()
		{
			Current.Value = previous;
		}
	}
}
