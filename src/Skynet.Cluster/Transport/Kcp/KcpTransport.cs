using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Core.Serialization;
using Skynet.Cluster.Transport.Kcp;

namespace Skynet.Cluster;

/// <summary>
/// Provides a UDP-based transport built on the KCP protocol (vendored managed implementation) for
/// cross-node actor communication. KCP supplies reliable, ordered, message-boundary preserving
/// delivery over UDP with latency-oriented tuning, making this transport an option for real-time
/// workloads where TCP's head-of-line blocking is undesirable.
/// </summary>
/// <remarks>
/// <para>
/// Every remote node is one KCP session identified by a 32-bit conversation id carried at the head
/// of each datagram. Frames ride the reliable KCP stream in stream order: a handshake exchange
/// (node id swap) precedes any envelope traffic. Framing mirrors <see cref="TcpTransport"/>
/// (handshake/envelope/heartbeat frame types, the same envelope serialization, pending-call
/// tracking, and heartbeat-based dead-peer detection) so the two transports are semantically
/// interchangeable from an <see cref="ActorSystem"/> perspective.
/// </para>
/// <para>
/// Unlike <see cref="TcpTransport"/>, this transport does not stack the protocol-independent
/// <see cref="Transport.Reliable.ReliableQueue"/> on top of KCP: KCP already provides reliability
/// and retransmission, so an additional layer would be pure overhead. ReliableQueue exists for
/// transports that receive no reliability from their underlying channel.
/// </para>
/// </remarks>
public sealed class KcpTransport : ITransport, IAsyncDisposable
{
	static KcpTransport()
	{
		// Fault responses can arrive before this node ever sends one itself; pre-register the
		// payload so incoming fault envelopes resolve through the contract table.
		PayloadContractRegistry.Register<KcpRemoteCallFault>();
	}

	private const int ConversationHeaderLength = KcpWire.ConversationHeaderLength;

	private readonly ActorSystem _system;
	private readonly IClusterRegistry _registry;
	private readonly KcpTransportOptions _options;
	private readonly ILogger<KcpTransport> _logger;
	private readonly UdpClient _udp;
	private readonly CancellationTokenSource _cts = new();
	private readonly ConcurrentDictionary<uint, KcpConnection> _sessionsByConv = new();
	private readonly ConcurrentDictionary<string, KcpConnection> _connections = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<long, PendingCall> _pendingCalls = new();
	private readonly MessagePackSerializerOptions _serializerOptions;
	private Task? _receiveLoop;
	private bool _disposed;

	public KcpTransport(ActorSystem system, IClusterRegistry registry, KcpTransportOptions? options = null,
		ILoggerFactory? loggerFactory = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_options = options ?? new KcpTransportOptions();
		_logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KcpTransport>();
		_serializerOptions = _options.SerializerOptions ?? MessagePackSerializerOptions.Standard;
		var localNodeId = registry.LocalNodeId ??
		                  throw new InvalidOperationException("The registry does not expose a local node identifier.");
		if (!_registry.TryGetNode(localNodeId, out var descriptor))
		{
			throw new InvalidOperationException($"Unable to locate node '{localNodeId}' in the registry.");
		}

		LocalNodeId = localNodeId;
		_udp = new UdpClient(descriptor.EndPoint);
		_receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
	}

	internal string LocalNodeId { get; }

	/// <inheritdoc />
	public async ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(KcpTransport));
		}

		if (_system.TryGetActorHost(envelope.To, out _))
		{
			await _system.DeliverLocalAsync(envelope, response, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (!_registry.TryResolveByHandle(envelope.To, out var location))
		{
			var exception =
				new InvalidOperationException(
					$"Unable to resolve actor handle {envelope.To.Value} through the cluster registry.");
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
					var (transport, messageId) = ((KcpTransport transport, long messageId))state!;
					if (transport._pendingCalls.TryRemove(messageId, out var pending))
					{
						pending.Response.TrySetCanceled();
						pending.Dispose();
					}
				}, (this, envelope.MessageId))
				: default;
			pending = new PendingCall(location.NodeId, response, registration);
			if (!_pendingCalls.TryAdd(envelope.MessageId, pending))
			{
				await registration.DisposeAsync();
				throw new InvalidOperationException(
					$"A pending call already exists for message id {envelope.MessageId}.");
			}
		}

		KcpConnection connection;
		try
		{
			connection = await EnsureConnectionAsync(location.NodeId, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A failed connect (unreachable peer, handshake rejected) must fail-fast the pending
			// call so the caller's TaskCompletionSource is never left dangling; concurrent calls
			// each remove their own entry, so batch failures cannot leak.
			if (pending is not null && _pendingCalls.TryRemove(envelope.MessageId, out var failed))
			{
				failed.Response.TrySetException(new IOException(
					$"Failed to establish a KCP session to node '{location.NodeId}'.", ex));
				failed.Dispose();
			}

			throw;
		}
		catch (OperationCanceledException)
		{
			// Cancellation (caller token or connect timeout) must surface as-is instead of being
			// masked as an IOException. The pending entry is still removed so a connect timeout on
			// a non-cancelable token cannot leak it; for cancellable tokens the pending's own
			// cancellation registration usually removes it first.
			if (pending is not null && _pendingCalls.TryRemove(envelope.MessageId, out var canceled))
			{
				canceled.Dispose();
			}

			throw;
		}

		// Scope the pending call to the session it will be sent on so a disconnect can fail
		// exactly the calls routed through the dying session (see OnConnectionClosed).
		pending?.Connection = connection;

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

	/// <summary>
	/// Sends a UDP datagram to the given endpoint. Called from connection pumps, so it must never
	/// block; failures are logged and dropped (KCP retransmits anyway).
	/// </summary>
	internal void SendDatagram(IPEndPoint endpoint, ReadOnlyMemory<byte> datagram)
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_udp.Client.SendTo(datagram.Span, endpoint);
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
		{
			if (!_disposed)
			{
				_logger.LogDebug(ex, "Failed to send a {Length}-byte UDP datagram to {EndPoint}.", datagram.Length,
					endpoint);
			}
		}
	}

	private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			UdpReceiveResult result;
			try
			{
				result = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed)
			{
				break;
			}
			catch (SocketException ex)
			{
				if (_disposed)
				{
					break;
				}

				_logger.LogWarning(ex, "UDP receive failed; continuing.");
				continue;
			}
			catch (ObjectDisposedException) when (_disposed)
			{
				break;
			}

			RouteDatagram(result);
		}
	}

	private void RouteDatagram(in UdpReceiveResult result)
	{
		var datagram = result.Buffer;
		if (datagram.Length <= ConversationHeaderLength)
		{
			_logger.LogDebug("Dropped {Length}-byte UDP datagram from {EndPoint}: too short to carry a conversation header.",
				datagram.Length, result.RemoteEndPoint);
			return;
		}

		var conversationId = BinaryPrimitives.ReadUInt32LittleEndian(datagram);
		var payload = datagram.AsMemory(ConversationHeaderLength);
		if (_sessionsByConv.TryGetValue(conversationId, out var session))
		{
			if (!session.RemoteEndPoint.Equals(result.RemoteEndPoint))
			{
				_logger.LogWarning(
					"Dropped a datagram for conversation {ConversationId}: origin {EndPoint} does not match the bound session endpoint {SessionEndPoint}.",
					conversationId, result.RemoteEndPoint, session.RemoteEndPoint);
				return;
			}

			_ = session.HandleDatagramAsync(payload.ToArray(), CancellationToken.None);
			return;
		}

		CreateInboundSession(conversationId, result.RemoteEndPoint, payload.ToArray());
	}

	private void CreateInboundSession(uint conversationId, IPEndPoint remoteEndPoint, byte[] firstPayload)
	{
		var connection = new KcpConnection(this, conversationId, remoteEndPoint, outbound: false, _logger, _options);
		if (!_sessionsByConv.TryAdd(conversationId, connection))
		{
			// A concurrent datagram created the session first; feed this payload into it.
			_ = _sessionsByConv[conversationId].HandleDatagramAsync(firstPayload, CancellationToken.None);
			return;
		}

		_logger.LogDebug("Accepted new KCP conversation {ConversationId} from {EndPoint}.", conversationId,
			remoteEndPoint);
		connection.Start();
		connection.StartHeartbeat(_cts.Token);
		_ = connection.HandleDatagramAsync(firstPayload, CancellationToken.None);
	}

	private async Task<KcpConnection> EnsureConnectionAsync(string nodeId, CancellationToken cancellationToken)
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
			if (_connections.TryGetValue(nodeId, out existing) && existing.IsAlive)
			{
				// An inbound session won the race while we were dialing; keep the existing session
				// so both peers agree on a single link and discard our outbound one.
				await connection.DisposeAsync().ConfigureAwait(false);
				return existing;
			}

			_connections[nodeId] = connection;
			connection.StartHeartbeat(_cts.Token);
			return connection;
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<KcpConnection> ConnectAsync(string nodeId, CancellationToken cancellationToken)
	{
		if (!_registry.TryGetNode(nodeId, out var descriptor))
		{
			throw new InvalidOperationException($"Cluster registry does not contain node '{nodeId}'.");
		}

		var endpoint = new IPEndPoint(descriptor.EndPoint.Address, descriptor.EndPoint.Port);
		var conversationId = AllocateConversationId();
		var connection = new KcpConnection(this, conversationId, endpoint, outbound: true, _logger, _options);
		_sessionsByConv[conversationId] = connection;
		try
		{
			await connection.ConnectAsync(_registry.LocalNodeId!, cancellationToken).ConfigureAwait(false);
			return connection;
		}
		catch
		{
			_sessionsByConv.TryRemove(conversationId, out _);
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private uint AllocateConversationId()
	{
		Span<byte> buffer = stackalloc byte[4];
		while (true)
		{
			Random.Shared.NextBytes(buffer);
			var conversationId = BitConverter.ToUInt32(buffer);
			if (conversationId != 0 && !_sessionsByConv.ContainsKey(conversationId))
			{
				return conversationId;
			}
		}
	}

	/// <summary>
	/// Registers an inbound session for its remote node once its handshake arrived, arbitrating
	/// against outbound sessions created by <see cref="EnsureConnectionAsync"/> through the same
	/// per-node gate. An existing healthy connection is always kept.
	/// </summary>
	/// <returns><c>true</c> when the session was registered; <c>false</c> when it was rejected in
	/// favor of an existing healthy connection.</returns>
	internal async Task<bool> TryRegisterInboundConnectionAsync(KcpConnection connection)
	{
		var gate = _connectionLocks.GetOrAdd(connection.RemoteNodeId, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(_cts.Token).ConfigureAwait(false);
		try
		{
			if (_connections.TryGetValue(connection.RemoteNodeId, out var existing) &&
				existing.IsAlive &&
				!ReferenceEquals(existing, connection))
			{
				return false;
			}

			_connections[connection.RemoteNodeId] = connection;
			connection.StartHeartbeat(_cts.Token);
			return true;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Completes the inbound handshake off the pump thread: registers the session (rejecting
	/// duplicates) and answers with the local node id.
	/// </summary>
	internal async Task CompleteInboundHandshakeAsync(KcpConnection connection)
	{
		if (!await TryRegisterInboundConnectionAsync(connection).ConfigureAwait(false))
		{
			_logger.LogInformation(
				"Rejected duplicate inbound KCP session from node {NodeId}: an active session already exists.",
				connection.RemoteNodeId);
			await connection.DisposeAsync().ConfigureAwait(false);
			return;
		}

		_logger.LogDebug("Accepted inbound KCP session from node {NodeId}.", connection.RemoteNodeId);
		await connection.SendFrameAsync(KcpFrameType.Handshake,
			MessagePackSerializer.Serialize(new KcpHandshake(LocalNodeId)), CancellationToken.None)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Fails every pending call that was routed through the given session with the supplied
	/// exception so no <see cref="TaskCompletionSource{TResult}"/> is left dangling after a
	/// disconnect.
	/// </summary>
	private void FailPendingCallsForNode(Exception exception, KcpConnection connection)
	{
		foreach (var pair in _pendingCalls)
		{
			if (!ReferenceEquals(pair.Value.Connection, connection))
			{
				continue;
			}

			if (_pendingCalls.TryRemove(pair.Key, out var pending))
			{
				using (pending)
				{
					pending.Response.TrySetException(exception);
				}
			}
		}
	}

	internal void OnConnectionClosed(KcpConnection connection)
	{
		_sessionsByConv.TryRemove(connection.ConversationId, out _);
		if (_disposed || !connection.TryGetRemoteNodeId(out var nodeId))
		{
			return;
		}

		// Atomic conditional remove (key + value): only succeeds when the registered session is
		// still this one, so a concurrently registered replacement can never be removed here.
		_connections.TryRemove(new KeyValuePair<string, KcpConnection>(nodeId, connection));

		// Fail every pending call that was routed through this session — regardless of whether it
		// was still the registered link, because its pump has ended and no response can ever
		// arrive on it. Pending calls carry a Connection reference, so this can never touch
		// pending calls belonging to a replacement session (and a pending registered after the
		// removal above is scoped to the replacement, not to this dead session). Pendings that
		// are still dialing (Connection not yet assigned) are intentionally not failed here; they
		// are handled by the connect failure path in SendAsync or by the caller's own timeout.
		FailPendingCallsForNode(new RemoteConnectionClosedException(nodeId), connection);
	}

	internal async Task HandleIncomingEnvelopeAsync(KcpConnection connection, MessageEnvelope envelope)
	{
		// Only response envelopes may complete a pending call. A request frame from the peer
		// carries a MessageId from the peer's own counter, which can collide with the ids of
		// this node's outstanding calls; matching it would corrupt the pending call's result
		// and silently swallow the request.
		if (envelope.IsResponse)
		{
			if (_pendingCalls.TryRemove(envelope.MessageId, out var pending))
			{
				using (pending)
				{
					switch (envelope.Payload)
					{
						case KcpRemoteCallFault { IsCancellation: true }:
							pending.Response.TrySetCanceled();
							break;
						case KcpRemoteCallFault fault:
							pending.Response.TrySetException(
								new RpcDispatchException(fault.Message ?? "Remote actor reported an error."));
							break;
						default:
							pending.Response.TrySetResult(envelope.Payload);
							break;
					}
				}

				return;
			}

			// A response with no pending call (the caller already timed out or the session was
			// replaced) has nowhere to go; never deliver it to a local actor as a request.
			_logger.LogWarning("Dropped a stale response for message {MessageId} from node {NodeId}.",
				envelope.MessageId, connection.RemoteNodeId);
			return;
		}

		TaskCompletionSource<object?>? responseSource = null;
		if (envelope.CallType == CallType.Call)
		{
			responseSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			_ = responseSource.Task.ContinueWith(
				async (task, state) =>
				{
					var (transport, conn, request) =
						((KcpTransport transport, KcpConnection conn, MessageEnvelope request))state!;
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
				_logger.LogError(ex, "Failed to deliver message {MessageId} from node {NodeId}.", envelope.MessageId,
					connection.RemoteNodeId);
			}
		}
	}

	private async Task SendResponseAsync(KcpConnection connection, MessageEnvelope request, Task<object?> responseTask)
	{
		MessageEnvelope response;
		if (responseTask.IsCanceled)
		{
			response = request.WithResponse(new KcpRemoteCallFault(true,
				typeof(OperationCanceledException).FullName ?? "System.OperationCanceledException",
				"Remote call was canceled."));
		}
		else if (responseTask.IsFaulted)
		{
			var exception = responseTask.Exception?.GetBaseException() ??
			                new InvalidOperationException("Remote call failed.");
			response = request.WithResponse(new KcpRemoteCallFault(false,
				exception.GetType().FullName ?? exception.GetType().Name, exception.Message));
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
			_logger.LogWarning(ex, "Failed to transmit response for message {MessageId} to node {NodeId}.",
				request.MessageId, connection.RemoteNodeId);
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
		await _cts.CancelAsync().ConfigureAwait(false);
		_udp.Close();
		if (_receiveLoop is not null)
		{
			try
			{
				await _receiveLoop.ConfigureAwait(false);
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

		foreach (var connection in _sessionsByConv.Values)
		{
			await connection.DisposeAsync().ConfigureAwait(false);
		}

		_sessionsByConv.Clear();
		_connections.Clear();
		_cts.Dispose();
	}

	private sealed class PendingCall : IDisposable
	{
		private readonly CancellationTokenRegistration _registration;

		internal PendingCall(string nodeId, TaskCompletionSource<object?> response,
			CancellationTokenRegistration registration)
		{
			NodeId = nodeId;
			Response = response;
			_registration = registration;
		}

		/// <summary>
		/// Gets the identifier of the node the call was routed to, used to fail pending calls
		/// when the connection to that node dies.
		/// </summary>
		internal string NodeId { get; }

		/// <summary>
		/// Gets or sets the session the call was sent on, assigned by <see cref="KcpTransport.SendAsync"/>
		/// once the session is established. Used by <see cref="KcpTransport.OnConnectionClosed"/> to
		/// fail exactly the calls routed through the dying session. <see langword="null"/> while the
		/// call is still establishing its session.
		/// </summary>
		internal KcpConnection? Connection { get; set; }

		internal TaskCompletionSource<object?> Response { get; }

		public void Dispose()
		{
			_registration.Dispose();
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record KcpRemoteCallFault(
		[property: Key(0)] bool IsCancellation,
		[property: Key(1)] string ExceptionType,
		[property: Key(2)] string? Message);
}

/// <summary>
/// Provides configuration for <see cref="KcpTransport"/>.
/// </summary>
public sealed class KcpTransportOptions
{
	/// <summary>
	/// Gets or sets the timeout for outbound KCP handshakes.
	/// </summary>
	public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Gets or sets the heartbeat interval used to keep sessions alive and detect dead peers.
	/// </summary>
	public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Gets or sets the maximum period a session may receive no frames at all before it is
	/// considered dead and closed. Defaults to <see cref="TimeSpan.Zero"/>, which means three
	/// times <see cref="HeartbeatInterval"/>. Only effective when <see cref="HeartbeatInterval"/>
	/// is greater than zero.
	/// </summary>
	public TimeSpan DeadNodeGracePeriod { get; init; } = TimeSpan.Zero;

	/// <summary>
	/// Gets or sets the maximum allowed frame payload size in bytes. KCP fragments large messages
	/// internally, but a single message can span at most 255 fragments (~288 KB at the default
	/// MTU), so frames beyond this limit are rejected up front. The default is 256 KB.
	/// </summary>
	public int MaxMessageBytes { get; init; } = 256 * 1024;

	/// <summary>
	/// Gets or sets the KCP update interval in milliseconds. Smaller intervals trade CPU for
	/// lower latency.
	/// </summary>
	public int IntervalMilliseconds { get; init; } = 10;

	/// <summary>
	/// Gets or sets a value indicating whether KCP runs in no-delay mode (recommended for
	/// real-time workloads).
	/// </summary>
	public bool NoDelay { get; init; } = true;

	/// <summary>
	/// Gets or sets the number of duplicate acknowledgements that trigger a fast retransmission
	/// before the RTO expires.
	/// </summary>
	public int FastResend { get; init; } = 2;

	/// <summary>
	/// Gets or sets a value indicating whether KCP congestion window control is disabled. Real-time
	/// workloads usually disable it to favor throughput over fairness.
	/// </summary>
	public bool DisableCongestionWindow { get; init; } = true;

	/// <summary>Gets or sets the KCP send window (segments in flight).</summary>
	public int SendWindow { get; init; } = 128;

	/// <summary>Gets or sets the KCP receive window (segments in flight).</summary>
	public int ReceiveWindow { get; init; } = 128;

	/// <summary>
	/// Gets or sets the serializer options applied to envelopes.
	/// </summary>
	public MessagePackSerializerOptions? SerializerOptions { get; init; }
}
