using Skynet.Core;

namespace Skynet.Extras;

/// <summary>
/// Abstraction used by the debug console to interact with the actor system.
/// </summary>
public interface IDebugConsoleActorGateway
{
	/// <summary>
	/// Gets the metrics collector backing the actor system.
	/// </summary>
	ActorMetricsCollector Metrics { get; }

	/// <summary>
	/// Lists the actors currently known to the runtime.
	/// </summary>
	IReadOnlyCollection<ActorDescriptor> ListActors();

	/// <summary>
	/// Attempts to resolve an actor handle from an identifier or name.
	/// </summary>
	bool TryResolveHandle(string identifier, out ActorHandle handle, out string? error);

	/// <summary>
	/// Terminates the actor associated with the specified handle.
	/// </summary>
	Task<bool> KillAsync(ActorHandle handle, CancellationToken cancellationToken);
}
