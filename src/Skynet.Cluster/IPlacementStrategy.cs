using Skynet.Core;

namespace Skynet.Cluster;

/// <summary>
/// Maps placement keys (e.g. "room:42") onto the set of participating cluster nodes.
/// </summary>
/// <remarks>
/// The strategy owns the participating node set: callers refresh it through
/// <see cref="UpdateNodes"/> when nodes join or leave and resolve individual keys through
/// <see cref="Place"/>. The abstraction is deliberately independent of
/// <see cref="IClusterRegistry"/> — it only decides which node <em>should</em> host a key and
/// stays out of the name/handle resolution path, so it can be composed with any registry
/// implementation.
/// </remarks>
public interface IPlacementStrategy
{
	/// <summary>
	/// Gets the current set of participating node identifiers, sorted in ordinal order.
	/// </summary>
	IReadOnlyList<string> Nodes { get; }

	/// <summary>
	/// Selects the node that should host the actor identified by <paramref name="key"/>.
	/// </summary>
	/// <param name="key">The stable placement key (e.g. "room:42").</param>
	/// <returns>The identifier of the target node.</returns>
	string Place(string key);

	/// <summary>
	/// Replaces the participating node set with <paramref name="nodeIds"/>.
	/// </summary>
	/// <param name="nodeIds">The new node set; must contain at least one distinct node id.</param>
	void UpdateNodes(IReadOnlyCollection<string> nodeIds);
}
