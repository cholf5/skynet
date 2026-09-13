using System.Buffers.Binary;
using System.Net;
using MessagePack;
using Microsoft.Extensions.Logging;
using Skynet.Core;
using Skynet.Core.Serialization;

namespace Skynet.Cluster.Transport.Kcp;

/// <summary>
/// Identifies the frame types carried as application messages inside the reliable KCP stream.
/// KCP preserves message boundaries, so a frame is a single type byte followed by the payload.
/// </summary>
internal enum KcpFrameType : byte
{
	Handshake = 1,
	Envelope = 2,
	Heartbeat = 3
}

/// <summary>Gets the size in bytes of the conversation id header prepended to every datagram.</summary>
internal static class KcpWire
{
	internal const int ConversationHeaderLength = 4;
}

[MessagePackObject(AllowPrivate = true)]
internal sealed record KcpHandshake([property: Key(0)] string NodeId);

/// <summary>
/// Represents one KCP session between this node and a remote peer, multiplexed over a shared
/// UDP socket. The instance delegates all KCP state-machine work to a dedicated
/// <see cref="KcpConnectionPump"/> and only performs framing, handshake bookkeeping, and
/// heartbeat-based dead-peer detection on top of it.
/// </summary>
internal sealed class KcpConnection : IAsyncDisposable
{
	private readonly KcpTransport _transport;
	private readonly ILogger _logger;
	private readonly KcpConnectionPump _pump;
	private readonly CancellationTokenSource _cts = new();
	private readonly TaskCompletionSource _handshakeCompleted =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private long _lastReceivedTickCount = Environment.TickCount64;
	private string? _remoteNodeId;
	private bool _disposed;

	internal KcpConnection(KcpTransport transport, uint conversationId, IPEndPoint remoteEndPoint,
		bool outbound, ILogger logger, KcpTransportOptions options)
	{
		_transport = transport;
		_logger = logger;
		ConversationId = conversationId;
		RemoteEndPoint = remoteEndPoint;
		Outbound = outbound;
		Options = options;
		var interval = TimeSpan.FromMilliseconds(Math.Max(1, options.IntervalMilliseconds));
		_pump = new KcpConnectionPump(
			new KcpSession(
				conversationId,
				options.IntervalMilliseconds,
				options.NoDelay,
				options.FastResend,
				options.DisableCongestionWindow,
				options.SendWindow,
				options.ReceiveWindow,
				(segment, length) => OutgoingSegment(segment, length)),
			interval,
			segment => OutgoingSegment(segment, segment.Length),
			OnFrame,
			ex => _ = HandleFaultAsync(ex));
	}

	private void OutgoingSegment(byte[] segment, int length)
	{
		// Every datagram is prefixed with the conversation id so the shared socket can route it.
		var datagram = new byte[KcpWire.ConversationHeaderLength + length];
		BinaryPrimitives.WriteUInt32LittleEndian(datagram, ConversationId);
		Array.Copy(segment, 0, datagram, KcpWire.ConversationHeaderLength, length);
		_transport.SendDatagram(RemoteEndPoint, datagram);
	}

	internal uint ConversationId { get; }

	internal IPEndPoint RemoteEndPoint { get; }

	internal bool Outbound { get; }

	private KcpTransportOptions Options { get; }

	internal string RemoteNodeId =>
		_remoteNodeId ?? throw new InvalidOperationException("Handshake not completed.");

	internal bool TryGetRemoteNodeId(out string nodeId)
	{
		nodeId = _remoteNodeId ?? string.Empty;
		return _remoteNodeId is not null;
	}

	internal bool IsAlive => !_cts.IsCancellationRequested;

	internal Task HandshakeTask => _handshakeCompleted.Task;

	/// <summary>
	/// Establishes the session for an outbound connection: starts the pump, sends the local node
	/// id, and waits for the peer's handshake response.
	/// </summary>
	internal async Task ConnectAsync(string localNodeId, CancellationToken cancellationToken)
	{
		_pump.Start();
		await SendFrameAsync(KcpFrameType.Handshake,
			MessagePackSerializer.Serialize(new KcpHandshake(localNodeId)), cancellationToken)
			.ConfigureAwait(false);
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (Options.ConnectTimeout > TimeSpan.Zero)
		{
			timeoutCts.CancelAfter(Options.ConnectTimeout);
		}

		try
		{
			await _handshakeCompleted.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Timed out waiting for the KCP handshake response from endpoint {RemoteEndPoint}.");
		}
	}

	/// <summary>Starts the pump for an inbound (server-side) session without awaiting a handshake.</summary>
	internal void Start()
	{
		_pump.Start();
	}

	/// <summary>
	/// Starts background keepalive/dead-peer detection. Heartbeats double as a liveness probe for
	/// the UDP path where a silent peer is otherwise indistinguishable from a routed-away peer.
	/// </summary>
	internal void StartHeartbeat(CancellationToken transportToken)
	{
		if (Options.HeartbeatInterval <= TimeSpan.Zero)
		{
			return;
		}

		var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, transportToken);
		_ = Task.Run(() => HeartbeatLoopAsync(linked.Token));
	}

	/// <summary>Feeds a datagram routed by conversation id into the pump.</summary>
	internal ValueTask HandleDatagramAsync(byte[] datagram, CancellationToken cancellationToken)
	{
		return _pump.InputAsync(datagram, cancellationToken);
	}

	internal async ValueTask SendEnvelopeAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
		var payload = MessageEnvelopeSerializer.Serialize(envelope, Options.SerializerOptions);
		await SendFrameAsync(KcpFrameType.Envelope, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Sends a raw frame. Also callable from the pump thread (non-blocking channel write).</summary>
	internal ValueTask SendFrameAsync(KcpFrameType type, byte[] payload, CancellationToken cancellationToken)
	{
		if (payload.Length > Options.MaxMessageBytes)
		{
			throw new InvalidOperationException(
				$"Frame payload of {payload.Length} bytes exceeds the maximum allowed KCP message size of {Options.MaxMessageBytes} bytes.");
		}

		var frame = new byte[1 + payload.Length];
		frame[0] = (byte)type;
		payload.CopyTo(frame, 1);
		return _pump.SendAsync(frame, cancellationToken);
	}

	/// <summary>Invoked on the pump thread for every complete application message in stream order.</summary>
	private void OnFrame(byte[] message)
	{
		Volatile.Write(ref _lastReceivedTickCount, Environment.TickCount64);
		if (message.Length == 0)
		{
			return;
		}

		var type = (KcpFrameType)message[0];
		switch (type)
		{
			case KcpFrameType.Handshake:
				HandleHandshake(MessagePackSerializer.Deserialize<KcpHandshake>(message.AsMemory(1, message.Length - 1),
					Options.SerializerOptions));
				break;
			case KcpFrameType.Envelope:
				MessageEnvelope envelope;
				try
				{
					envelope = MessageEnvelopeSerializer.Deserialize(message.AsMemory(1, message.Length - 1),
						Options.SerializerOptions);
				}
				catch (UnknownPayloadContractException ex)
				{
					// A peer with a different contract set sent an unknown payload id. Reject the
					// frame but keep the session alive for future traffic.
					_logger.LogError(ex,
						"Rejected KCP envelope from node {NodeId}: payload contract id {ContractId} is not registered on this node.",
						_remoteNodeId, ex.ContractId);
					return;
				}

				_ = Task.Run(() => _transport.HandleIncomingEnvelopeAsync(this, envelope));
				break;
			case KcpFrameType.Heartbeat:
				break;
			default:
				_logger.LogWarning("Received unknown KCP frame type {FrameType} from {NodeId}.", (byte)type,
					_remoteNodeId);
				break;
		}
	}

	private void HandleHandshake(KcpHandshake handshake)
	{
		if (_remoteNodeId is not null)
		{
			return;
		}

		_remoteNodeId = handshake.NodeId;
		if (Outbound)
		{
			_handshakeCompleted.TrySetResult();
			return;
		}

		// Registration and the handshake reply are continued off the pump thread so the KCP state
		// machine never blocks on transport bookkeeping.
		_ = _transport.CompleteInboundHandshakeAsync(this);
	}

	private async Task HandleFaultAsync(Exception reason)
	{
		_logger.LogWarning(reason, "KCP pump for node {NodeId} failed; closing the session.", _remoteNodeId);
		await DisposeAsync().ConfigureAwait(false);
	}

	private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(Options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);

				var silentFor = TimeSpan.FromMilliseconds(Environment.TickCount64 -
					Interlocked.Read(ref _lastReceivedTickCount));
				if (Options.DeadNodeGracePeriod > TimeSpan.Zero && silentFor > Options.DeadNodeGracePeriod)
				{
					_logger.LogWarning(
						"No frames received from node {NodeId} for {SilentFor} (grace period {GracePeriod}); closing the KCP session as suspected dead.",
						_remoteNodeId, silentFor, Options.DeadNodeGracePeriod);
					_ = DisposeAsync();
					return;
				}

				await SendFrameAsync(KcpFrameType.Heartbeat, [], cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		if (!_cts.IsCancellationRequested)
		{
			await _cts.CancelAsync().ConfigureAwait(false);
		}

		_handshakeCompleted.TrySetCanceled();
		await _pump.DisposeAsync().ConfigureAwait(false);
		_cts.Dispose();
		_transport.OnConnectionClosed(this);
	}
}
