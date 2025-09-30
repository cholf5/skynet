using System.Net;

namespace Skynet.Core;

/// <summary>
/// Provides lookup services for mapping actors to cluster nodes.
/// </summary>
public interface IClusterRegistry
{
	/// <summary>
	/// Gets the logical identifier of the local node.
	/// </summary>
	string? LocalNodeId { get; }

	/// <summary>
	/// Attempts to resolve a named actor to a location in the cluster.
	/// </summary>
	bool TryResolveByName(string name, out ClusterActorLocation location);

	/// <summary>
	/// Attempts to resolve an actor handle to a location in the cluster.
	/// </summary>
	bool TryResolveByHandle(ActorHandle handle, out ClusterActorLocation location);

	/// <summary>
	/// Attempts to retrieve the descriptor for the specified node.
	/// </summary>
	bool TryGetNode(string nodeId, out ClusterNodeDescriptor descriptor);

	/// <summary>
	/// Records that the specified actor is hosted on the local node.
	/// </summary>
	void RegisterLocalActor(string name, ActorHandle handle);

	/// <summary>
	/// Removes the registration for the specified actor from the local node.
	/// </summary>
	void UnregisterLocalActor(string name, ActorHandle handle);
}

/// <summary>
/// Describes the location of an actor inside the cluster.
/// </summary>
public sealed record ClusterActorLocation(string NodeId, ActorHandle Handle);

/// <summary>
/// Describes a cluster node and its transport endpoint.
/// </summary>
public sealed record ClusterNodeDescriptor(string NodeId, IPEndPoint EndPoint);
