using System.Collections.Concurrent;
using System.Net;
using Skynet.Core;

namespace Skynet.Cluster;

/// <summary>
/// Represents the static cluster configuration.
/// </summary>
public sealed class StaticClusterConfiguration
{
/// <summary>
/// Gets or sets the configured nodes.
/// </summary>
public IReadOnlyCollection<StaticClusterNodeConfiguration> Nodes { get; init; } = Array.Empty<StaticClusterNodeConfiguration>();
}

/// <summary>
/// Represents a statically configured cluster node.
/// </summary>
public sealed class StaticClusterNodeConfiguration
{
/// <summary>
/// Gets or sets the unique node identifier.
/// </summary>
public string NodeId { get; init; } = string.Empty;

/// <summary>
/// Gets or sets the hostname or IP address that the node listens on.
/// </summary>
public string Host { get; init; } = "127.0.0.1";

/// <summary>
/// Gets or sets the TCP port used by the node.
/// </summary>
public int Port { get; init; }

/// <summary>
/// Gets or sets the base handle offset reserved for the node.
/// </summary>
public long HandleOffset { get; init; }

/// <summary>
/// Gets or sets the mapping of service names to well-known actor handles.
/// </summary>
public IDictionary<string, long> Services { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary>
/// Provides a static implementation of <see cref="IClusterRegistry"/> backed by configuration.
/// </summary>
public sealed class StaticClusterRegistry : IClusterRegistry
{
private readonly Dictionary<string, ClusterNodeDescriptor> _nodes;
private readonly Dictionary<string, long> _serviceToHandle;
private readonly Dictionary<long, string> _handleToNode;
private readonly object _sync = new();
private readonly string _localNodeId;

public StaticClusterRegistry(StaticClusterConfiguration configuration, string localNodeId)
{
ArgumentNullException.ThrowIfNull(configuration);
ArgumentException.ThrowIfNullOrEmpty(localNodeId);
if (configuration.Nodes.Count == 0)
{
throw new ArgumentException("At least one node must be configured.", nameof(configuration));
}

_localNodeId = localNodeId;
_nodes = new Dictionary<string, ClusterNodeDescriptor>(StringComparer.Ordinal);
_serviceToHandle = new Dictionary<string, long>(StringComparer.Ordinal);
_handleToNode = new Dictionary<long, string>();

foreach (var node in configuration.Nodes)
{
ValidateNode(node);
var endpoint = ResolveEndpoint(node.Host, node.Port);
_nodes[node.NodeId] = new ClusterNodeDescriptor(node.NodeId, endpoint);
foreach (var pair in node.Services)
{
_serviceToHandle[pair.Key] = pair.Value;
_handleToNode[pair.Value] = node.NodeId;
}
}

if (!_nodes.ContainsKey(localNodeId))
{
throw new ArgumentException($"Local node '{localNodeId}' is not part of the configuration.", nameof(localNodeId));
}
}

/// <inheritdoc />
public string? LocalNodeId => _localNodeId;

/// <inheritdoc />
public bool TryResolveByName(string name, out ClusterActorLocation location)
{
ArgumentException.ThrowIfNullOrEmpty(name);
lock (_sync)
{
if (_serviceToHandle.TryGetValue(name, out var handle) && _handleToNode.TryGetValue(handle, out var nodeId))
{
location = new ClusterActorLocation(nodeId, new ActorHandle(handle));
return true;
}
}

location = default!;
return false;
}

/// <inheritdoc />
public bool TryResolveByHandle(ActorHandle handle, out ClusterActorLocation location)
{
if (!handle.IsValid)
{
location = default!;
return false;
}

lock (_sync)
{
if (_handleToNode.TryGetValue(handle.Value, out var nodeId))
{
location = new ClusterActorLocation(nodeId, handle);
return true;
}
}

location = default!;
return false;
}

/// <inheritdoc />
public bool TryGetNode(string nodeId, out ClusterNodeDescriptor descriptor)
{
ArgumentException.ThrowIfNullOrEmpty(nodeId);
return _nodes.TryGetValue(nodeId, out descriptor!);
}

/// <inheritdoc />
public void RegisterLocalActor(string name, ActorHandle handle)
{
ArgumentException.ThrowIfNullOrEmpty(name);
if (!handle.IsValid)
{
throw new ArgumentException("Handle must be valid.", nameof(handle));
}

lock (_sync)
{
if (_serviceToHandle.TryGetValue(name, out var configured) && configured != handle.Value)
{
throw new InvalidOperationException($"Service '{name}' is configured with handle {configured} but attempted to register with {handle.Value}.");
}

_serviceToHandle[name] = handle.Value;
_handleToNode[handle.Value] = _localNodeId;
}
}

/// <inheritdoc />
public void UnregisterLocalActor(string name, ActorHandle handle)
{
ArgumentException.ThrowIfNullOrEmpty(name);
if (!handle.IsValid)
{
return;
}

lock (_sync)
{
if (_serviceToHandle.TryGetValue(name, out var configured) && configured == handle.Value)
{
_serviceToHandle.Remove(name);
_handleToNode.Remove(handle.Value);
}
}
}

private static void ValidateNode(StaticClusterNodeConfiguration node)
{
ArgumentNullException.ThrowIfNull(node);
ArgumentException.ThrowIfNullOrEmpty(node.NodeId);
ArgumentException.ThrowIfNullOrEmpty(node.Host);
if (node.Port <= 0 || node.Port > 65535)
{
throw new ArgumentOutOfRangeException(nameof(node.Port), node.Port, "Port must be between 1 and 65535.");
}
}

private static IPEndPoint ResolveEndpoint(string host, int port)
{
if (IPAddress.TryParse(host, out var address))
{
return new IPEndPoint(address, port);
}

var addresses = Dns.GetHostAddresses(host);
if (addresses.Length == 0)
{
throw new InvalidOperationException($"Unable to resolve host '{host}'.");
}

return new IPEndPoint(addresses[0], port);
}
}
