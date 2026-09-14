using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Core.Serialization;

namespace Skynet.Transport.Kcp;

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
/// (node id swap) precedes any envelope traffic. Framing mirrors the TCP transport
/// (<c>TcpTransport</c> in Skynet.Cluster; handshake/envelope/heartbeat frame types, the same
/// envelope serialization, pending-call tracking, and heartbeat-based dead-peer detection) so the
/// two transports are semantically interchangeable from an <see cref="ActorSystem"/> perspective.
/// </para>
/// <para>
/// Unlike the TCP transport, this transport does not stack the protocol-independent
/// <see cref="Reliable.ReliableQueue"/> on top of KCP: KCP already provides reliability
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
	// Internal for white-box tests (mirrors TcpTransport._connections/_pendingCalls).
	internal readonly ConcurrentDictionary<uint, KcpConnection> _sessionsByConv = new();
	internal readonly ConcurrentDictionary<string, KcpConnection> _connections = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new(StringComparer.Ordinal);
	/// <summary>
	/// Pending remote calls keyed by message id. Internal for test introspection; do not mutate
	/// outside of the pending-call lifecycle paths (mirrors TcpTransport._pendingCalls).
	/// </summary>
	internal readonly ConcurrentDictionary<long, PendingCall> _pendingCalls = new();
	/// <summary>Retired conversation ids mapped to the raw tick count at which the entry expires.</summary>
	private readonly ConcurrentDictionary<uint, long> _retiredConversations = new();
	private int _inboundSessionCount;
	private readonly MessagePackSerializerOptions _serializerOptions;
	private Task? _receiveLoop;
	// 0 = live, 1 = disposed; guarded with Interlocked so concurrent dispose calls cannot both
	// run the teardown sequence (same pattern as KcpConnection._disposed).
	private int _disposed;

	public KcpTransport(ActorSystem system, IClusterRegistry registry, KcpTransportOptions? options = null,
		ILoggerFactory? loggerFactory = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_options = options ?? new KcpTransportOptions();
		if (_options.MaxMessageBytes <= 0)
		{
			// Non-positive values would silently disable the message size guard; "no limit" must
			// be an explicit int.MaxValue instead of an accidental default(T) (mirrors the
			// TcpTransportOptions.MaxFrameBytes validation).
			throw new ArgumentOutOfRangeException(nameof(options), _options.MaxMessageBytes,
				"MaxMessageBytes must be positive; pass int.MaxValue to disable the message size limit.");
		}

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
		if (_disposed != 0)
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
		if (_disposed != 0)
		{
			return;
		}

		try
		{
			_udp.Client.SendTo(datagram.Span, endpoint);
		}
		catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
		{
			if (_disposed == 0)
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
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed != 0)
			{
				break;
			}
			catch (SocketException ex)
			{
				if (_disposed != 0)
				{
					break;
				}

				_logger.LogWarning(ex, "UDP receive failed; continuing.");
				continue;
			}
			catch (ObjectDisposedException) when (_disposed != 0)
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

		if (IsRetiredConversation(conversationId))
		{
			_logger.LogWarning(
				"Dropped a datagram for retired conversation {ConversationId} from {EndPoint}: that session was already closed and must not be resurrected.",
				conversationId, result.RemoteEndPoint);
			return;
		}

		CreateInboundSession(conversationId, result.RemoteEndPoint, payload.ToArray());
	}

	/// <summary>
	/// Checks whether the conversation id belongs to a session that was already closed. Entries are
	/// removed lazily: only when another datagram probes the same conversation id after its
	/// retention has expired is the stale entry deleted. Ids that never see traffic again stay in
	/// the table — growth is bounded by session churn (one entry per closed conversation), not by
	/// a background sweep.
	/// </summary>
	private bool IsRetiredConversation(uint conversationId)
	{
		if (!_retiredConversations.TryGetValue(conversationId, out var expiresAtTickCount))
		{
			return false;
		}

		if (Environment.TickCount64 < expiresAtTickCount)
		{
			return true;
		}

		_retiredConversations.TryRemove(conversationId, out _);
		return false;
	}

	private void CreateInboundSession(uint conversationId, IPEndPoint remoteEndPoint, byte[] firstPayload)
	{
		// Bound the session table before paying for a pump + tasks + timer: unknown conversation
		// ids arrive unauthenticated, so a forged source could otherwise create sessions that are
		// only ever reclaimed by timeouts. Existing sessions are never evicted by the limit.
		if (_options.MaxInboundSessions > 0 &&
			Interlocked.Increment(ref _inboundSessionCount) > _options.MaxInboundSessions)
		{
			Interlocked.Decrement(ref _inboundSessionCount);
			_logger.LogWarning(
				"Rejected a new inbound KCP conversation {ConversationId} from {EndPoint}: the inbound session limit of {MaxInboundSessions} is reached.",
				conversationId, remoteEndPoint, _options.MaxInboundSessions);
			return;
		}

		var connection = new KcpConnection(this, conversationId, remoteEndPoint, outbound: false, _logger, _options);
		if (!_sessionsByConv.TryAdd(conversationId, connection))
		{
			// A concurrent datagram created the session first; feed this payload into it.
			Interlocked.Decrement(ref _inboundSessionCount);
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
			if (conversationId != 0 && !_sessionsByConv.ContainsKey(conversationId) &&
				!_retiredConversations.ContainsKey(conversationId))
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
		if (!connection.Outbound)
		{
			// Mirror the reservation made by CreateInboundSession so the inbound session limit keeps
			// reflecting only live sessions. Fires exactly once per connection (guarded by the
			// connection's own dispose guard).
			Interlocked.Decrement(ref _inboundSessionCount);
		}

		// Retire the conversation id so late datagrams for this dead session cannot resurrect it
		// as a fresh inbound session (the session-resurrection hole).
		_retiredConversations[connection.ConversationId] =
			Environment.TickCount64 + _options.RetiredConversationRetention.Ticks;

		if (_disposed != 0 || !connection.TryGetRemoteNodeId(out var nodeId))
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

	/// <summary>
	/// Handles an envelope whose payload contract id is not registered on this node. Request frames
	/// that expect a response (<c>CallType.Call</c>) are answered with a
	/// <see cref="KcpRemoteCallFault"/> so the caller's pending call fails at RTT speed instead of
	/// hanging until the session dies. Response frames carry this node's own message id, so the
	/// matching pending call is failed locally — no reply is ever sent for an unreadable frame: a
	/// fault we cannot read must not trigger another fault, or two peers with mismatched contract
	/// sets would loop forever on each other's fault responses. Mirrors the equivalent handler of
	/// the TCP transport (<c>TcpTransport.HandleUnknownPayloadContractAsync</c>).
	/// </summary>
	internal async Task HandleUnknownPayloadContractAsync(KcpConnection connection, MessageEnvelope request,
		int contractId)
	{
		_logger.LogError(
			"Rejected KCP envelope from node {NodeId}: payload contract id {ContractId} is not registered on this node.",
			connection.RemoteNodeId, contractId);

		if (request.IsResponse)
		{
			// The pending call is local (response frames are matched against this node's own
			// pending calls only), so it can be failed directly with zero loop-back risk. This
			// converts a call that would otherwise hang until timeout/disconnect into an
			// immediate failure with the same surface as a remote-answered fault.
			if (_pendingCalls.TryRemove(request.MessageId, out var pending))
			{
				using (pending)
				{
					pending.Response.TrySetException(new RpcDispatchException(
						$"Payload contract id {contractId} is not registered on this node; the response could not be decoded. " +
						"Ensure all nodes share the same contract assembly and register the payload type."));
				}
			}

			return;
		}

		if (request.CallType != CallType.Call)
		{
			// Fire-and-forget requests leave no pending call on the peer to fail fast.
			return;
		}

		var fault = new KcpRemoteCallFault(
			IsCancellation: false,
			ExceptionType: typeof(UnknownPayloadContractException).FullName ?? "UnknownPayloadContractException",
			Message: $"Payload contract id {contractId} is not registered on this node; the request was dropped. " +
				"Ensure all nodes share the same contract assembly and register the payload type.");
		try
		{
			await connection.SendEnvelopeAsync(request.WithResponse(fault), _cts.Token)
				.ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception,
				"Failed to transmit the unknown-contract fault for message {MessageId} to node {NodeId}.",
				request.MessageId, connection.RemoteNodeId);
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
		// Atomic check-and-set: concurrent dispose calls (e.g. user code racing a receive-loop
		// failure path) must let exactly one caller run the cancellation/teardown sequence.
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

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

	internal sealed class PendingCall : IDisposable
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
	/// Gets or sets the maximum period an inbound session may exist without completing its
	/// handshake before it is closed. Outbound sessions are bounded by <see cref="ConnectTimeout"/>;
	/// without this deadline an inbound session created by an unauthenticated datagram could pin
	/// its pump, tasks, and timer forever when the peer never finishes the handshake.
	/// Defaults to 10 seconds. Set to <see cref="TimeSpan.Zero"/> to disable the watchdog.
	/// </summary>
	public TimeSpan InboundHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Gets or sets the maximum number of concurrently maintained inbound sessions. Datagrams that
	/// would create an inbound session beyond this limit are dropped (existing sessions are never
	/// evicted), which keeps unauthenticated peers from growing the session table without bound.
	/// Defaults to 256. Set to zero or a negative value to disable the limit.
	/// </summary>
	public int MaxInboundSessions { get; init; } = 256;

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
	/// Gets or sets how long the conversation id of a closed session is remembered as retired. Late
	/// datagrams for a retired conversation are dropped instead of creating a new inbound session,
	/// so a peer reply that arrives after the local session already died (e.g. a handshake response
	/// that outlived the outbound <see cref="ConnectTimeout"/>) cannot resurrect the dead session
	/// under an attacker-controlled source endpoint. Defaults to 5 minutes. Set to
	/// <see cref="TimeSpan.Zero"/> to disable conversation retirement (late datagrams may then
	/// resurrect closed sessions).
	/// </summary>
	public TimeSpan RetiredConversationRetention { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Gets or sets the maximum allowed frame payload size in bytes. KCP fragments large messages
	/// internally, but a single message can span at most 255 fragments (~288 KB at the default
	/// MTU), so frames beyond this limit are rejected up front. The default is 256 KB. Must be
	/// positive; pass <see cref="int.MaxValue"/> to disable the message size limit.
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
