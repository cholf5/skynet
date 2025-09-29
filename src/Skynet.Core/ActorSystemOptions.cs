namespace Skynet.Core;

/// <summary>
/// Provides configuration for an <see cref="ActorSystem"/>.
/// </summary>
public sealed class ActorSystemOptions
{
	/// <summary>
	/// Gets or sets the logical identifier of the current node.
	/// </summary>
	public string? NodeId { get; init; }

	/// <summary>
	/// Gets or sets the offset applied to automatically generated actor handles.
	/// </summary>
	public long HandleOffset { get; init; }

	/// <summary>
	/// Gets or sets the cluster registry used to resolve remote actors.
	/// </summary>
	public IClusterRegistry? ClusterRegistry { get; init; }
}
