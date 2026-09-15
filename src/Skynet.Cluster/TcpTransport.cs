using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
	static TcpTransport()
	{
		// Fault responses can arrive before this node ever sends one itself; pre-register the
		// payload so incoming fault envelopes resolve through the contract table.
		PayloadContractRegistry.Register<RemoteCallFault>();
	}

	private readonly ActorSystem _system;
	private readonly IClusterRegistry _registry;
	private readonly TcpTransportOptions _options;
	private readonly ILogger<TcpTransport> _logger;
	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _cts = new();
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new(StringComparer.Ordinal);

	/// <summary>
	/// Pending remote calls keyed by message id. Internal for test introspection; do not mutate
	/// outside of the pending-call lifecycle paths.
	/// </summary>
	internal readonly ConcurrentDictionary<long, PendingCall> _pendingCalls = new();

	/// <summary>
	/// Live connections keyed by remote node id. Internal for test introspection; do not mutate
	/// outside of the connection lifecycle paths.
	/// </summary>
	internal readonly ConcurrentDictionary<string, TcpConnection> _connections = new(StringComparer.Ordinal);
	private readonly MessagePackSerializerOptions _serializerOptions;
	private readonly TimeSpan _deadNodeGracePeriod;
	private readonly Task? _acceptLoop;
	private bool _disposed;

	public TcpTransport(ActorSystem system, IClusterRegistry registry, TcpTransportOptions? options = null,
		ILoggerFactory? loggerFactory = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_options = options ?? new TcpTransportOptions();
		if (_options.MaxFrameBytes <= 0)
		{
			// Non-positive values silently disabled the frame size guard; "no limit" must be an
			// explicit int.MaxValue instead of an accidental default(T) or miscomputed constant.
			throw new ArgumentOutOfRangeException(nameof(options), _options.MaxFrameBytes,
				"MaxFrameBytes must be positive; pass int.MaxValue to disable the frame size limit.");
		}

		_logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TcpTransport>();
		_serializerOptions = _options.SerializerOptions ?? MessagePackSerializerOptions.Standard;
		_deadNodeGracePeriod = DeadNodeGracePeriod.Resolve(_options.DeadNodeGracePeriod, _options.HeartbeatInterval);
		var localNodeId = registry.LocalNodeId ??
		                  throw new InvalidOperationException("The registry does not expose a local node identifier.");
		if (!_registry.TryGetNode(localNodeId, out var descriptor))
		{
			throw new InvalidOperationException($"Unable to locate node '{localNodeId}' in the registry.");
		}

		_listener = new TcpListener(descriptor.EndPoint);
		_listener.Start();
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
	}

	/// <inheritdoc />
	public async ValueTask SendAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response,
		CancellationToken cancellationToken = default)
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
					var (transport, messageId) = ((TcpTransport transport, long messageId))state!;
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

		TcpConnection connection;
		try
		{
			connection = await EnsureConnectionAsync(location.NodeId, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A failed connect (refused, handshake rejected) must fail-fast the pending call so the
			// caller's TaskCompletionSource is never left dangling; concurrent calls each remove
			// their own entry, so batch failures cannot leak.
			if (pending is not null && _pendingCalls.TryRemove(envelope.MessageId, out var failed))
			{
				failed.Response.TrySetException(new IOException(
					$"Failed to establish a connection to node '{location.NodeId}'.", ex));
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

		// Scope the pending call to the connection it will be sent on so a disconnect can fail
		// exactly the calls routed through the dying connection (see OnConnectionClosed).
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
			if (_connections.TryGetValue(nodeId, out existing) && existing.IsAlive)
			{
				// An inbound connection won the race while we were dialing; keep the existing
				// connection so both peers agree on a single link and discard our outbound one.
				await connection.DisposeAsync().ConfigureAwait(false);
				return existing;
			}

			_connections[nodeId] = connection;
			connection.Start(_cts.Token);
			return connection;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Registers an inbound connection for its remote node, arbitrating against outbound
	/// connections created by <see cref="EnsureConnectionAsync"/> through the same per-node gate.
	/// Without this arbitration the inbound write to <c>_connections</c> would blindly overwrite
	/// an already healthy outbound connection (last-write-wins), leaving two live links to the
	/// same node. An existing healthy connection is always kept and the new inbound connection
	/// is rejected.
	/// </summary>
	/// <returns><c>true</c> when the connection was registered; <c>false</c> when it was rejected
	/// in favor of an existing healthy connection.</returns>
	private async Task<bool> TryRegisterInboundConnectionAsync(TcpConnection connection)
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
			return true;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Cleans up after a connection's read loop has ended. The registered connection slot is only
	/// released when this connection is still the registered link: a replacement connection may
	/// already own the node after a reconnect, and removing it would be an ABA error.
	/// </summary>
	internal void OnConnectionClosed(TcpConnection connection)
	{
		if (!connection.TryGetRemoteNodeId(out var nodeId))
		{
			return;
		}

		// Atomic conditional remove (key + value): only succeeds when the registered connection is
		// still this one, so a concurrently registered replacement can never be removed here.
		_connections.TryRemove(new KeyValuePair<string, TcpConnection>(nodeId, connection));

		// Fail every pending call that was routed through this connection — regardless of whether
		// it was still the registered link, because its read loop has ended and no response can
		// ever arrive on it. Pending calls carry a Connection reference, so this can never touch
		// pending calls belonging to a replacement connection (and a pending registered after the
		// removal above is scoped to the replacement, not to this dead connection). Pendings that
		// are still dialing (Connection not yet assigned) are intentionally not failed here; they
		// are handled by the connect failure path in SendAsync or by the caller's own timeout.
		FailPendingCallsForNode(new RemoteConnectionClosedException(nodeId), connection);
	}

	/// <summary>
	/// Fails every pending call that was routed through the given connection with the supplied
	/// exception so no <see cref="TaskCompletionSource{TResult}"/> is left dangling after a
	/// disconnect.
	/// </summary>
	private void FailPendingCallsForNode(Exception exception, TcpConnection connection)
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

		await client.ConnectAsync(descriptor.EndPoint.Address, descriptor.EndPoint.Port, connectCts.Token)
			.ConfigureAwait(false);
		// Client TLS settings are resolved per dial: the target host names the dialed endpoint so
		// SNI and certificate name validation see the registry address.
		SslClientAuthenticationOptions? tlsClientOptions = null;
		if (_options.UseTls)
		{
			tlsClientOptions = new SslClientAuthenticationOptions
			{
				TargetHost = descriptor.EndPoint.Address.ToString(),
				RemoteCertificateValidationCallback = _options.RemoteCertificateValidationCallback
			};
		}

		var connection = new TcpConnection(this, client, outbound: true, _logger, _options.HeartbeatInterval,
			_deadNodeGracePeriod, _options.MaxFrameBytes, _serializerOptions, tlsClientOptions: tlsClientOptions,
			streamDecorator: _options.StreamDecorator);
		try
		{
			await connection.InitializeAsync(_registry.LocalNodeId!, connectCts.Token).ConfigureAwait(false);
		}
		catch
		{
			// A failed TLS or cluster handshake must not leak the socket: dispose the connection
			// (streams plus client) before surfacing the failure to the dialer.
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}

		return connection;
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				TcpClient? client;
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
					TcpConnection? connection = null;
					try
					{
						connection = new TcpConnection(this, client!, outbound: false, _logger,
							_options.HeartbeatInterval, _deadNodeGracePeriod, _options.MaxFrameBytes,
							_serializerOptions, _options.TlsServerCertificate,
							streamDecorator: _options.StreamDecorator);
						await connection.InitializeAsync(_registry.LocalNodeId!, _cts.Token).ConfigureAwait(false);
						if (await TryRegisterInboundConnectionAsync(connection).ConfigureAwait(false))
						{
							connection.Start(_cts.Token);
						}
						else
						{
							_logger.LogInformation(
								"Rejected duplicate inbound connection from node {NodeId}: an active connection already exists.",
								connection.RemoteNodeId);
							await connection.DisposeAsync().ConfigureAwait(false);
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to process incoming connection.");
						if (connection is not null)
						{
							// Same disposal contract as the outbound path in ConnectAsync: disposing the
							// connection releases the streams (SslStream included) and the client, plus
							// the connection's one-shot resources (_cts, _writeLock). DisposeCore is
							// idempotent, so this is safe even after a partial teardown.
							await connection.DisposeAsync().ConfigureAwait(false);
						}
						else
						{
							// The constructor itself failed before a connection existed.
							client?.Dispose();
						}
					}
				}, cancellationToken);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "TCP transport accept loop terminated unexpectedly.");
		}
	}

	private async Task HandleIncomingEnvelopeAsync(TcpConnection connection, MessageEnvelope envelope)
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
						case RemoteCallFault { IsCancellation: true }:
							pending.Response.TrySetCanceled();
							break;
						case RemoteCallFault fault:
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

			// A response with no pending call (the caller already timed out or the connection was
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
						((TcpTransport transport, TcpConnection conn, MessageEnvelope request))state!;
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

	private async Task SendResponseAsync(TcpConnection connection, MessageEnvelope request, Task<object?> responseTask)
	{
		MessageEnvelope response;
		if (responseTask.IsCanceled)
		{
			response = request.WithResponse(new RemoteCallFault(true,
				typeof(OperationCanceledException).FullName ?? "System.OperationCanceledException",
				"Remote call was canceled."));
		}
		else if (responseTask.IsFaulted)
		{
			var exception = responseTask.Exception?.GetBaseException() ??
			                new InvalidOperationException("Remote call failed.");
			response = request.WithResponse(new RemoteCallFault(false,
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

	/// <summary>
	/// Handles an envelope whose payload contract id is not registered on this node. Request frames
	/// that expect a response (<c>CallType.Call</c>) are answered with a <see cref="RemoteCallFault"/>
	/// so the caller's pending call fails at RTT speed instead of hanging until the connection dies.
	/// Response frames carry this node's own message id, so the matching pending call is failed
	/// locally — no reply is ever sent for an unreadable frame: a fault we cannot read must not
	/// trigger another fault, or two peers with mismatched contract sets would loop forever on each
	/// other's fault responses.
	/// </summary>
	internal async Task HandleUnknownPayloadContractAsync(TcpConnection connection, MessageEnvelope request,
		int contractId)
	{
		_logger.LogError(
			"Rejected envelope from node {NodeId}: payload contract id {ContractId} is not registered on this node.",
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

		var fault = new RemoteCallFault(
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

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await _cts.CancelAsync();
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
		internal string NodeId
		{
			get;
		}

		/// <summary>
		/// Gets or sets the connection the call was sent on, assigned by <see cref="TcpTransport.SendAsync"/>
		/// once the connection is established. Used by <see cref="TcpTransport.OnConnectionClosed"/> to
		/// fail exactly the calls routed through the dying connection. <see langword="null"/> while the
		/// call is still establishing its connection.
		/// </summary>
		internal TcpConnection? Connection
		{
			get;
			set;
		}

		internal TaskCompletionSource<object?> Response
		{
			get;
		}

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
	internal sealed record RemoteCallFault(
		[property: Key(0)] bool IsCancellation,
		[property: Key(1)] string ExceptionType,
		[property: Key(2)] string? Message);

	internal sealed class TcpConnection : IAsyncDisposable
	{
		private readonly TcpTransport _transport;
		private readonly TcpClient _client;
		private readonly NetworkStream _networkStream;
		private readonly X509Certificate2? _tlsServerCertificate;
		private readonly SslClientAuthenticationOptions? _tlsClientOptions;
		private readonly Func<Stream, Stream>? _streamDecorator;
		private Stream _stream;
		private readonly bool _outbound;
		private readonly ILogger _logger;
		private readonly TimeSpan _heartbeatInterval;
		private readonly TimeSpan _deadNodeGracePeriod;
		private readonly int _maxFrameBytes;
		private readonly MessagePackSerializerOptions _serializerOptions;
		private readonly SemaphoreSlim _writeLock = new(1, 1);
		private readonly CancellationTokenSource _cts = new();
		private long _lastReceivedTickCount = Environment.TickCount64;
		private Task? _readLoop;
		private Task? _heartbeatLoop;
		private string? _remoteNodeId;
		private bool _disposed;

		internal TcpConnection(TcpTransport transport, TcpClient client, bool outbound, ILogger logger,
			TimeSpan heartbeatInterval, TimeSpan deadNodeGracePeriod, int maxFrameBytes,
			MessagePackSerializerOptions serializerOptions,
			X509Certificate2? tlsServerCertificate = null,
			SslClientAuthenticationOptions? tlsClientOptions = null,
			Func<Stream, Stream>? streamDecorator = null)
		{
			_transport = transport;
			_client = client;
			_outbound = outbound;
			_logger = logger;
			_heartbeatInterval = heartbeatInterval;
			_deadNodeGracePeriod = deadNodeGracePeriod;
			_maxFrameBytes = maxFrameBytes;
			_serializerOptions = serializerOptions;
			_tlsServerCertificate = tlsServerCertificate;
			_tlsClientOptions = tlsClientOptions;
			_streamDecorator = streamDecorator;
			_networkStream = client.GetStream();
			_stream = _networkStream;
		}

		internal string RemoteNodeId =>
			_remoteNodeId ?? throw new InvalidOperationException("Handshake not completed.");

		/// <summary>
		/// Gets a value indicating whether the handshake completed and the remote node id is known.
		/// </summary>
		internal bool TryGetRemoteNodeId([MaybeNullWhen(false)] out string nodeId)
		{
			nodeId = _remoteNodeId;
			return nodeId is not null;
		}

		internal bool IsAlive => !_cts.IsCancellationRequested && _client.Connected;

		internal async Task InitializeAsync(string localNodeId, CancellationToken cancellationToken)
		{
			// TLS (when configured) must complete before any cluster frame is written or read so no
			// plaintext handshake bytes can leak onto the wire and the peer's framing stays intact.
			if (_outbound)
			{
				if (_tlsClientOptions is not null)
				{
					await AuthenticateTlsAsClientAsync(cancellationToken).ConfigureAwait(false);
				}

				ApplyStreamDecorator();
				await SendHandshakeAsync(localNodeId, cancellationToken).ConfigureAwait(false);
				var handshake = await ReadHandshakeAsync(cancellationToken).ConfigureAwait(false);
				_remoteNodeId = handshake.NodeId;
			}
			else
			{
				if (_tlsServerCertificate is not null)
				{
					await AuthenticateTlsAsServerAsync(cancellationToken).ConfigureAwait(false);
				}

				ApplyStreamDecorator();
				var handshake = await ReadHandshakeAsync(cancellationToken).ConfigureAwait(false);
				_remoteNodeId = handshake.NodeId;
				await SendHandshakeAsync(localNodeId, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Wraps the connection stream in the decorator configured through
		/// <see cref="TcpTransportOptions.StreamDecorator"/> (test-only fault injection). Runs once
		/// after the TLS layer is in place and before any cluster frame is exchanged, so the
		/// decorator sees exactly the framed cluster protocol traffic.
		/// </summary>
		private void ApplyStreamDecorator()
		{
			if (_streamDecorator is null)
			{
				return;
			}

			_stream = _streamDecorator(_stream);
		}

		/// <summary>
		/// Wraps the raw socket stream in a client-side <see cref="SslStream"/> and completes the TLS
		/// handshake against the remote node. Runs before the cluster handshake; a failed handshake
		/// disposes the stream and rethrows so the whole connection is torn down.
		/// </summary>
		private async Task AuthenticateTlsAsClientAsync(CancellationToken cancellationToken)
		{
			var sslStream = new SslStream(_networkStream, leaveInnerStreamOpen: false);
			try
			{
				await sslStream.AuthenticateAsClientAsync(_tlsClientOptions!, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				await sslStream.DisposeAsync().ConfigureAwait(false);
				_logger.LogWarning(exception, "TLS handshake as client failed before the cluster handshake.");
				throw;
			}

			_stream = sslStream;
		}

		/// <summary>
		/// Wraps the raw socket stream in a server-side <see cref="SslStream"/> and completes the TLS
		/// handshake with the dialing node using the configured certificate. Runs before the cluster
		/// handshake; a failed handshake disposes the stream and rethrows so the whole connection is
		/// torn down.
		/// </summary>
		private async Task AuthenticateTlsAsServerAsync(CancellationToken cancellationToken)
		{
			var sslStream = new SslStream(_networkStream, leaveInnerStreamOpen: false);
			try
			{
				await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
				{
					ServerCertificate = _tlsServerCertificate!
				}, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				await sslStream.DisposeAsync().ConfigureAwait(false);
				_logger.LogWarning(exception, "TLS handshake as server failed before the cluster handshake.");
				throw;
			}

			_stream = sslStream;
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
							MessageEnvelope? envelope;
							int unknownContractId;
							// Unparseable frames return false (dropped below); unknown payload
							// contract ids come back through unknownContractId. Legacy wire versions
							// throw NotSupportedException, which tears the connection down — mixed
							// version clusters must keep failing loudly.
							if (!MessageEnvelopeSerializer.TryDeserialize(payload, out envelope,
								out unknownContractId, _serializerOptions))
							{
								// The frame is unparseable (it may itself be the peer's fault
								// response). Drop it and keep the connection alive; never reply
								// to a frame we could not read — that would create a fault loop
								// with a peer that also cannot parse our reply.
								_logger.LogWarning(
									"Dropped an unparseable envelope frame from node {NodeId}.",
									_remoteNodeId);
								continue;
							}

							if (unknownContractId != PayloadContractRegistry.NullPayloadContractId)
							{
								// A peer with a different contract set sent an unknown payload id.
								// Reject the frame but keep the connection alive for future traffic;
								// requests that expect a response are answered with a fault so the
								// caller fails fast instead of hanging until disconnect.
								await _transport.HandleUnknownPayloadContractAsync(this, envelope,
									unknownContractId).ConfigureAwait(false);
								continue;
							}

							await _transport.HandleIncomingEnvelopeAsync(this, envelope).ConfigureAwait(false);
							break;
						case FrameType.Heartbeat:
							break;
						case FrameType.Handshake:
							break;
						default:
							_logger.LogWarning("Received unknown frame type {FrameType} from {NodeId}.", type,
								_remoteNodeId);
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
				// Conditional cleanup: a replacement connection may already be registered for this
				// node (reconnect race), so only shared state owned by this connection is released.
				_transport.OnConnectionClosed(this);
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

					// Dead-node detection: if the peer has not sent any frame (including
					// heartbeats) for longer than the grace period, treat the link as half-open
					// and close it. Cancellation also terminates the read loop, which then
					// cleans up the connection and fails its pending calls.
					var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 -
						Interlocked.Read(ref _lastReceivedTickCount));
					if (_deadNodeGracePeriod > TimeSpan.Zero && silentFor > _deadNodeGracePeriod)
					{
						_logger.LogWarning(
							"No frames received from node {NodeId} for {SilentFor} (grace period {GracePeriod}); closing the connection as suspected dead.",
							_remoteNodeId, silentFor, _deadNodeGracePeriod);
						CancelConnection();
						return;
					}

					await WriteFrameAsync(FrameType.Heartbeat, ReadOnlyMemory<byte>.Empty, cancellationToken)
						.ConfigureAwait(false);
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

		private async Task WriteFrameAsync(FrameType type, ReadOnlyMemory<byte> payload,
			CancellationToken cancellationToken)
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
			// Any received frame (handshake, envelope, or heartbeat) proves the link is alive.
			Interlocked.Exchange(ref _lastReceivedTickCount, Environment.TickCount64);

			var lengthBuffer = new byte[4];
			await ReadExactAsync(lengthBuffer, cancellationToken).ConfigureAwait(false);
			var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));
			if (length < 0)
			{
				_logger.LogWarning(
					"Rejected negative frame length {FrameLength} from node {NodeId}; closing the connection.",
					length, _remoteNodeId);
				throw new InvalidOperationException("Negative frame length encountered.");
			}

			if (length > _maxFrameBytes)
			{
				// Hard-reject oversized frames before allocating anything so a malicious peer
				// cannot force huge allocations (OOM); the connection is torn down immediately.
				_logger.LogWarning(
					"Rejected {FrameLength}-byte frame from node {NodeId}: it exceeds MaxFrameBytes {MaxFrameBytes}; closing the connection.",
					length, _remoteNodeId, _maxFrameBytes);
				throw new IOException(
					$"Frame length {length} exceeds the maximum allowed frame size of {_maxFrameBytes} bytes.");
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
				var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
					.ConfigureAwait(false);
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
			// Disposing the (possibly TLS-wrapped) stream first also closes the inner network
			// stream; the client dispose below is then a harmless no-op for the socket.
			_stream.Dispose();
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
	public TimeSpan ConnectTimeout
	{
		get;
		init;
	} = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Gets or sets the heartbeat interval used to keep connections alive.
	/// </summary>
	public TimeSpan HeartbeatInterval
	{
		get;
		init;
	} = TimeSpan.FromSeconds(15);

	/// <summary>
	/// Gets or sets the maximum allowed frame payload size in bytes. A frame that announces a
	/// larger payload is rejected and the connection is closed to prevent malicious peers from
	/// forcing huge allocations. The default is 16 MB. Must be positive; pass
	/// <see cref="int.MaxValue"/> to disable the frame size limit.
	/// </summary>
	public int MaxFrameBytes
	{
		get;
		init;
	} = 16 * 1024 * 1024;

	/// <summary>
	/// Gets or sets the maximum period a connection may receive no frames at all before it is
	/// considered dead (half-open) and closed. Defaults to <see cref="TimeSpan.Zero"/>, which
	/// means three times <see cref="HeartbeatInterval"/>. Only effective when
	/// <see cref="HeartbeatInterval"/> is greater than zero.
	/// </summary>
	public TimeSpan DeadNodeGracePeriod
	{
		get;
		init;
	} = TimeSpan.Zero;

	/// <summary>
	/// Gets or sets the serializer options applied to envelopes.
	/// </summary>
	public MessagePackSerializerOptions? SerializerOptions
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets the certificate presented to dialing nodes on inbound (accepted) connections.
	/// When set, every accepted connection is upgraded to TLS (<see cref="SslStream"/>) and must
	/// complete the TLS handshake before the cluster handshake; when <see langword="null"/> (the
	/// default) inbound connections stay plaintext. Nodes dialing this node must enable
	/// <see cref="UseTls"/> — a dialer that does not will fail its connection (see
	/// <see cref="UseTls"/> for the mismatch semantics).
	/// </summary>
	public X509Certificate2? TlsServerCertificate
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets a value indicating whether outbound connections negotiate TLS
	/// (<see cref="SslStream"/>) with the remote node before sending the cluster handshake. The
	/// default is <see langword="false"/> (plaintext). The dialed node must serve TLS
	/// (<see cref="TlsServerCertificate"/> set): a TLS client against a plaintext server — or a
	/// plaintext client against a TLS server — never completes its handshake, so the connection
	/// fails within <see cref="ConnectTimeout"/> (the TLS handshake is part of the bounded connect
	/// sequence) instead of hanging.
	/// </summary>
	public bool UseTls
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets the callback used to validate the remote node's certificate during the outbound
	/// TLS handshake. Return <see langword="true"/> to accept the certificate and
	/// <see langword="false"/> to reject it, which fails the connection. When
	/// <see langword="null"/> (the default), the platform's standard chain and name validation
	/// applies. Only effective when <see cref="UseTls"/> is <see langword="true"/>; inbound TLS
	/// never invokes this callback (client certificates are not requested).
	/// </summary>
	public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets an optional decorator invoked once per connection with the fully established
	/// stream (TLS-wrapped when configured) before any cluster frame is exchanged, and applied to
	/// both inbound and outbound connections. The returned stream replaces the connection stream,
	/// which enables transport-level fault injection (for example
	/// <see cref="FaultInjectingStream"/> for chaos testing: frame loss, write delay, half-open
	/// links, and manual disconnects). When <see langword="null"/> (the default) the connection
	/// stream is used unchanged. The callback must return a non-null stream; an exception it
	/// throws fails the connection during establishment.
	/// </summary>
	public Func<Stream, Stream>? StreamDecorator
	{
		get;
		init;
	}
}
