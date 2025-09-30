using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Core.Serialization;

namespace Skynet.Cluster;

/// <summary>
/// Provides a TCP-based transport for cross-node actor communication.
/// </summary>
public sealed class TcpTransport : ITransport, IAsyncDisposable
{
private readonly ActorSystem _system;
private readonly IClusterRegistry _registry;
private readonly TcpTransportOptions _options;
private readonly ILogger<TcpTransport> _logger;
private readonly TcpListener _listener;
private readonly CancellationTokenSource _cts = new();
private readonly ConcurrentDictionary<string, TcpConnection> _connections = new(StringComparer.Ordinal);
private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new(StringComparer.Ordinal);
private readonly ConcurrentDictionary<long, PendingCall> _pendingCalls = new();
private readonly MessagePackSerializerOptions _serializerOptions;
private Task? _acceptLoop;
private bool _disposed;

public TcpTransport(ActorSystem system, IClusterRegistry registry, TcpTransportOptions? options = null, ILoggerFactory? loggerFactory = null)
{
_system = system ?? throw new ArgumentNullException(nameof(system));
_registry = registry ?? throw new ArgumentNullException(nameof(registry));
_options = options ?? new TcpTransportOptions();
_logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TcpTransport>();
_serializerOptions = _options.SerializerOptions ?? MessagePackSerializerOptions.Standard;
var localNodeId = registry.LocalNodeId ?? throw new InvalidOperationException("The registry does not expose a local node identifier.");
if (!_registry.TryGetNode(localNodeId, out var descriptor))
{
throw new InvalidOperationException($"Unable to locate node '{localNodeId}' in the registry.");
}

_listener = new TcpListener(descriptor.EndPoint);
_listener.Start();
_acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
}

/// <inheritdoc />
public async ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response, CancellationToken cancellationToken = default)
{
cancellationToken.ThrowIfCancellationRequested();
if (_disposed)
{
throw new ObjectDisposedException(nameof(TcpTransport));
}

if (_system.TryGetActorHost(envelope.To, out _))
{
await _system.DeliverLocalAsync(envelope, response, cancellationToken).ConfigureAwait(false);
return;
}

if (!_registry.TryResolveByHandle(envelope.To, out var location))
{
var exception = new InvalidOperationException($"Unable to resolve actor handle {envelope.To.Value} through the cluster registry.");
response?.TrySetException(exception);
throw exception;
}

if (string.Equals(location.NodeId, _registry.LocalNodeId, StringComparison.Ordinal))
{
await _system.DeliverLocalAsync(envelope, response, cancellationToken).ConfigureAwait(false);
return;
}

PendingCall? pending = null;
if (response is not null)
{
var registration = cancellationToken.CanBeCanceled
? cancellationToken.Register(static state =>
{
var (transport, messageId) = ((TcpTransport transport, long messageId))state!;
if (transport._pendingCalls.TryRemove(messageId, out var pending))
{
pending.Response.TrySetCanceled();
pending.Dispose();
}
}, (this, envelope.MessageId))
: default;
pending = new PendingCall(response, registration);
if (!_pendingCalls.TryAdd(envelope.MessageId, pending))
{
registration.Dispose();
throw new InvalidOperationException($"A pending call already exists for message id {envelope.MessageId}.");
}
}

var connection = await EnsureConnectionAsync(location.NodeId, cancellationToken).ConfigureAwait(false);
try
{
await connection.SendEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
}
catch
{
if (pending is not null && _pendingCalls.TryRemove(envelope.MessageId, out var removed))
{
removed.Response.TrySetException(new IOException("Failed to send envelope to remote node."));
removed.Dispose();
}
throw;
}
}

private async Task<TcpConnection> EnsureConnectionAsync(string nodeId, CancellationToken cancellationToken)
{
if (_connections.TryGetValue(nodeId, out var existing) && existing.IsAlive)
{
return existing;
}

var gate = _connectionLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
try
{
if (_connections.TryGetValue(nodeId, out existing) && existing.IsAlive)
{
return existing;
}

var connection = await ConnectAsync(nodeId, cancellationToken).ConfigureAwait(false);
_connections[nodeId] = connection;
connection.Start(_cts.Token);
return connection;
}
finally
{
gate.Release();
}
}

private async Task<TcpConnection> ConnectAsync(string nodeId, CancellationToken cancellationToken)
{
if (!_registry.TryGetNode(nodeId, out var descriptor))
{
throw new InvalidOperationException($"Cluster registry does not contain node '{nodeId}'.");
}

var client = new TcpClient();
var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
if (_options.ConnectTimeout > TimeSpan.Zero)
{
connectCts.CancelAfter(_options.ConnectTimeout);
}

await client.ConnectAsync(descriptor.EndPoint.Address, descriptor.EndPoint.Port, connectCts.Token).ConfigureAwait(false);
var connection = new TcpConnection(this, client, outbound: true, _logger, _options.HeartbeatInterval, _serializerOptions);
await connection.InitializeAsync(_registry.LocalNodeId!, connectCts.Token).ConfigureAwait(false);
return connection;
}

private async Task AcceptLoopAsync(CancellationToken cancellationToken)
{
try
{
while (!cancellationToken.IsCancellationRequested)
{
TcpClient? client = null;
try
{
client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
break;
}

_ = Task.Run(async () =>
{
try
{
var connection = new TcpConnection(this, client!, outbound: false, _logger, _options.HeartbeatInterval, _serializerOptions);
await connection.InitializeAsync(_registry.LocalNodeId!, _cts.Token).ConfigureAwait(false);
_connections[connection.RemoteNodeId] = connection;
connection.Start(_cts.Token);
}
catch (Exception ex)
{
_logger.LogWarning(ex, "Failed to process incoming connection.");
client?.Dispose();
}
}, cancellationToken);
}
}
catch (Exception ex)
{
_logger.LogError(ex, "TCP transport accept loop terminated unexpectedly.");
}
}

internal async Task HandleIncomingEnvelopeAsync(TcpConnection connection, MessageEnvelope envelope)
{
if (_pendingCalls.TryRemove(envelope.MessageId, out var pending))
{
using (pending)
{
switch (envelope.Payload)
{
case RemoteCallFault fault when fault.IsCancellation:
pending.Response.TrySetCanceled();
break;
case RemoteCallFault fault:
pending.Response.TrySetException(new RpcDispatchException(fault.Message ?? "Remote actor reported an error."));
break;
default:
pending.Response.TrySetResult(envelope.Payload);
break;
}
}
return;
}

TaskCompletionSource<object?>? responseSource = null;
if (envelope.CallType == CallType.Call)
{
responseSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
_ = responseSource.Task.ContinueWith(
async (task, state) =>
{
var (transport, conn, request) = ((TcpTransport transport, TcpConnection conn, MessageEnvelope request))state!;
await transport.SendResponseAsync(conn, request, task).ConfigureAwait(false);
},
(this, connection, envelope),
CancellationToken.None,
TaskContinuationOptions.ExecuteSynchronously,
TaskScheduler.Default);
}

try
{
await _system.DeliverLocalAsync(envelope, responseSource, _cts.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
if (responseSource is not null)
{
responseSource.TrySetException(ex);
}
else
{
_logger.LogError(ex, "Failed to deliver message {MessageId} from node {NodeId}.", envelope.MessageId, connection.RemoteNodeId);
}
}
}

private async Task SendResponseAsync(TcpConnection connection, MessageEnvelope request, Task<object?> responseTask)
{
MessageEnvelope response;
if (responseTask.IsCanceled)
{
response = request.WithResponse(new RemoteCallFault(true, typeof(OperationCanceledException).FullName ?? "System.OperationCanceledException", "Remote call was canceled."));
}
else if (responseTask.IsFaulted)
{
var exception = responseTask.Exception?.GetBaseException() ?? new InvalidOperationException("Remote call failed.");
response = request.WithResponse(new RemoteCallFault(false, exception.GetType().FullName ?? exception.GetType().Name, exception.Message));
}
else
{
response = request.WithResponse(responseTask.Result!);
}

try
{
await connection.SendEnvelopeAsync(response, _cts.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
_logger.LogWarning(ex, "Failed to transmit response for message {MessageId} to node {NodeId}.", request.MessageId, connection.RemoteNodeId);
}
}

/// <inheritdoc />
public async ValueTask DisposeAsync()
{
if (_disposed)
{
return;
}

_disposed = true;
_cts.Cancel();
_listener.Stop();
if (_acceptLoop is not null)
{
try
{
await _acceptLoop.ConfigureAwait(false);
}
catch (OperationCanceledException)
{
}
}

foreach (var pending in _pendingCalls.Values)
{
pending.Response.TrySetCanceled();
pending.Dispose();
}
_pendingCalls.Clear();

foreach (var connection in _connections.Values)
{
await connection.DisposeAsync().ConfigureAwait(false);
}
_connections.Clear();
_cts.Dispose();
}

private sealed class PendingCall : IDisposable
{
private readonly CancellationTokenRegistration _registration;

internal PendingCall(TaskCompletionSource<object?> response, CancellationTokenRegistration registration)
{
Response = response;
_registration = registration;
}

internal TaskCompletionSource<object?> Response { get; }

public void Dispose()
{
_registration.Dispose();
}
}

private enum FrameType : byte
{
Handshake = 1,
Envelope = 2,
Heartbeat = 3
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record ClusterHandshake([property: Key(0)] string NodeId);

[MessagePackObject(AllowPrivate = true)]
internal sealed record RemoteCallFault([property: Key(0)] bool IsCancellation, [property: Key(1)] string ExceptionType, [property: Key(2)] string? Message);

internal sealed class TcpConnection : IAsyncDisposable
{
private readonly TcpTransport _transport;
private readonly TcpClient _client;
private readonly NetworkStream _stream;
private readonly bool _outbound;
private readonly ILogger _logger;
private readonly TimeSpan _heartbeatInterval;
private readonly MessagePackSerializerOptions _serializerOptions;
private readonly SemaphoreSlim _writeLock = new(1, 1);
private readonly CancellationTokenSource _cts = new();
private Task? _readLoop;
private Task? _heartbeatLoop;
private string? _remoteNodeId;
private bool _disposed;

internal TcpConnection(TcpTransport transport, TcpClient client, bool outbound, ILogger logger, TimeSpan heartbeatInterval, MessagePackSerializerOptions serializerOptions)
{
_transport = transport;
_client = client;
_outbound = outbound;
_logger = logger;
_heartbeatInterval = heartbeatInterval;
_serializerOptions = serializerOptions;
_stream = client.GetStream();
}

internal string RemoteNodeId => _remoteNodeId ?? throw new InvalidOperationException("Handshake not completed.");

internal bool IsAlive => !_cts.IsCancellationRequested && _client.Connected;

internal async Task InitializeAsync(string localNodeId, CancellationToken cancellationToken)
{
if (_outbound)
{
await SendHandshakeAsync(localNodeId, cancellationToken).ConfigureAwait(false);
var handshake = await ReadHandshakeAsync(cancellationToken).ConfigureAwait(false);
_remoteNodeId = handshake.NodeId;
}
else
{
var handshake = await ReadHandshakeAsync(cancellationToken).ConfigureAwait(false);
_remoteNodeId = handshake.NodeId;
await SendHandshakeAsync(localNodeId, cancellationToken).ConfigureAwait(false);
}
}

internal void Start(CancellationToken transportToken)
{
var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, transportToken);
_readLoop = Task.Run(() => ReadLoopAsync(linked.Token), linked.Token);
if (_heartbeatInterval > TimeSpan.Zero)
{
_heartbeatLoop = Task.Run(() => HeartbeatLoopAsync(linked.Token), linked.Token);
}
}

internal async Task SendEnvelopeAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
{
var payload = MessageEnvelopeSerializer.Serialize(envelope, _serializerOptions);
await WriteFrameAsync(FrameType.Envelope, payload, cancellationToken).ConfigureAwait(false);
}

private async Task ReadLoopAsync(CancellationToken cancellationToken)
{
try
{
while (!cancellationToken.IsCancellationRequested)
{
var (type, payload) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
switch (type)
{
case FrameType.Envelope:
var envelope = MessageEnvelopeSerializer.Deserialize(payload, _serializerOptions);
await _transport.HandleIncomingEnvelopeAsync(this, envelope).ConfigureAwait(false);
break;
case FrameType.Heartbeat:
break;
case FrameType.Handshake:
break;
default:
_logger.LogWarning("Received unknown frame type {FrameType} from {NodeId}.", type, _remoteNodeId);
break;
}
}
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
}
catch (Exception ex)
{
_logger.LogWarning(ex, "TCP connection with node {NodeId} closed unexpectedly.", _remoteNodeId);
}
finally
{
if (_remoteNodeId is not null)
{
_transport._connections.TryRemove(_remoteNodeId, out _);
}
DisposeCore();
}
}

private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
{
try
{
while (!cancellationToken.IsCancellationRequested)
{
await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
await WriteFrameAsync(FrameType.Heartbeat, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
}
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
}
}

private async Task SendHandshakeAsync(string nodeId, CancellationToken cancellationToken)
{
var payload = MessagePackSerializer.Serialize(new ClusterHandshake(nodeId), _serializerOptions);
await WriteFrameAsync(FrameType.Handshake, payload, cancellationToken).ConfigureAwait(false);
}

private async Task<ClusterHandshake> ReadHandshakeAsync(CancellationToken cancellationToken)
{
while (true)
{
var (type, payload) = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
if (type == FrameType.Handshake)
{
return MessagePackSerializer.Deserialize<ClusterHandshake>(payload, _serializerOptions);
}
}
}

private async Task WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
{
await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
try
{
_stream.WriteByte((byte)type);
var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
await _stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
if (!payload.IsEmpty)
{
await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
}
}
finally
{
_writeLock.Release();
}
}

private async Task<(FrameType, ReadOnlyMemory<byte>)> ReadFrameAsync(CancellationToken cancellationToken)
{
var typeBuffer = new byte[1];
await ReadExactAsync(typeBuffer, cancellationToken).ConfigureAwait(false);
var typeByte = typeBuffer[0];

var lengthBuffer = new byte[4];
await ReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));
if (length < 0)
{
throw new InvalidOperationException("Negative frame length encountered.");
}

if (length == 0)
{
return ((FrameType)typeByte, ReadOnlyMemory<byte>.Empty);
}

var buffer = new byte[length];
await ReadExactAsync(buffer, cancellationToken).ConfigureAwait(false);
return ((FrameType)typeByte, buffer);
}

private async Task ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
{
var offset = 0;
while (offset < buffer.Length)
{
var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
if (read == 0)
{
throw new IOException("Connection closed while reading frame data.");
}
offset += read;
}
}

private void CancelConnection()
{
try
{
_cts.Cancel();
}
catch (ObjectDisposedException)
{
}
}

private void DisposeCore()
{
if (_disposed)
{
return;
}

_disposed = true;
CancelConnection();
_writeLock.Dispose();
_cts.Dispose();
_client.Dispose();
}

public ValueTask DisposeAsync()
{
DisposeCore();
return ValueTask.CompletedTask;
}
}
}

/// <summary>
/// Provides configuration for <see cref="TcpTransport"/>.
/// </summary>
public sealed class TcpTransportOptions
{
/// <summary>
/// Gets or sets the timeout for outbound TCP connections.
/// </summary>
public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

/// <summary>
/// Gets or sets the heartbeat interval used to keep connections alive.
/// </summary>
public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

/// <summary>
/// Gets or sets the serializer options applied to envelopes.
/// </summary>
public MessagePackSerializerOptions? SerializerOptions { get; init; }
}
