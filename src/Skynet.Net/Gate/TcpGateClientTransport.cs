using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Net;

/// <summary>
/// Real TCP implementation of <see cref="IGateClientTransport"/>. Parses "host:port" addresses
/// and frames every payload as <c>[4-byte big-endian length][payload]</c> — the same framing the
/// inbound <see cref="GateServer"/> uses on its TCP channel, so the pool can talk to a Skynet
/// GateServer directly.
/// </summary>
public sealed class TcpGateClientTransport : IGateClientTransport
{
	private readonly TcpGateClientTransportOptions _options;
	private readonly int _maxFrameBytes;
	private readonly ILogger _logger;

	public TcpGateClientTransport(TcpGateClientTransportOptions? options = null, ILogger? logger = null)
	{
		_options = options ?? new TcpGateClientTransportOptions();
		_options.Validate();

		_maxFrameBytes = _options.MaxFrameBytes;
		_logger = logger ?? NullLogger.Instance;
	}

	public async Task<IGateProxyConnection> ConnectAsync(GateEndpoint endpoint,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		var (host, port) = ParseEndpoint(endpoint.Address);

		var client = new TcpClient();
		try
		{
			await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			client.Dispose();
			throw;
		}

		return new TcpGateProxyConnection(endpoint, client, _options, _logger);
	}

	private static (string Host, int Port) ParseEndpoint(string endpoint)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
		var parts = endpoint.Split(':', 2);
		if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[1], out var port))
		{
			throw new FormatException($"Gate endpoint '{endpoint}' must be in host:port format.");
		}

		return (parts[0], port);
	}
}

/// <summary>
/// <see cref="IGateProxyConnection"/> backed by a <see cref="TcpClient"/>. Owns the socket
/// lifecycle for one connect attempt, runs an inbound receive pump that raises
/// <see cref="IGateProxyConnection.FrameReceived"/>, and raises
/// <see cref="IGateProxyConnection.Disconnected"/> exactly once when the socket drops.
/// Outbound writes are serialized under a send lock so concurrent senders cannot interleave a
/// header with another frame's payload (same shape as the inbound <see cref="TcpSessionConnection"/>).
/// </summary>
public sealed class TcpGateProxyConnection : IGateProxyConnection
{
	private readonly TcpClient _client;
	private readonly object _sync = new();
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly int _maxFrameBytes;
	private readonly ILogger _logger;
	private bool _isConnected = true;
	private bool _closeRaised;
	private bool _disposed;

	internal TcpGateProxyConnection(GateEndpoint endpoint, TcpClient client,
		TcpGateClientTransportOptions options, ILogger? logger, bool startReceiveLoop = true)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		ArgumentNullException.ThrowIfNull(options);
		_client = client ?? throw new ArgumentNullException(nameof(client));
		ConnectionId = $"{endpoint.Name}:{Guid.NewGuid():N}";
		// Copy the settings out up front: the options object is mutable and shared, and the
		// connection must be immune to later configuration changes.
		_maxFrameBytes = options.MaxFrameBytes;
		_logger = logger ?? NullLogger.Instance;
		if (startReceiveLoop)
		{
			_ = RunReceiveLoopAsync();
		}
	}

	public string ConnectionId { get; }

	public bool IsConnected
	{
		get
		{
			lock (_sync)
			{
				return _isConnected && !_closeRaised;
			}
		}
	}

	public event EventHandler<GateDisconnectedEventArgs>? Disconnected;

	public event EventHandler<GateFrameReceivedEventArgs>? FrameReceived;

	public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
	{
		lock (_sync)
		{
			if (!_isConnected || _closeRaised)
			{
				throw new InvalidOperationException("Gate connection is closed.");
			}
		}

		var stream = _client.GetStream();
		await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// Once the lock is held the frame is committed: header, payload and flush all run
			// with CancellationToken.None so a cancelled send can never tear the stream in the
			// middle of a frame — a torn length-prefixed frame would silently desynchronize
			// every later frame on this connection and could slip past the frame size limit.
			// The caller's token is still honored while waiting for the lock and at the frame
			// boundary check below.
			cancellationToken.ThrowIfCancellationRequested();
			var header = new byte[4];
			BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
			await stream.WriteAsync(header).ConfigureAwait(false);
			if (payload.Length > 0)
			{
				await stream.WriteAsync(payload).ConfigureAwait(false);
			}

			await stream.FlushAsync().ConfigureAwait(false);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	public ValueTask DisposeAsync()
	{
		bool alreadyDisposed;
		lock (_sync)
		{
			alreadyDisposed = _disposed;
			_disposed = true;
		}

		if (alreadyDisposed)
		{
			return ValueTask.CompletedTask;
		}

		// Disposing the socket aborts in-flight writes with IOException so lock holders exit
		// promptly and release the semaphore, waking senders queued on WaitAsync. The semaphore
		// itself is never disposed: it holds no unmanaged resources (AvailableWaitHandle is
		// never touched) and disposing it would strand senders queued on WaitAsync forever.
		((IDisposable)_client).Dispose();
		return ValueTask.CompletedTask;
	}

	private async Task RunReceiveLoopAsync()
	{
		var stream = _client.GetStream();
		var header = new byte[4];
		try
		{
			while (true)
			{
				if (!await ReadExactAsync(stream, header).ConfigureAwait(false))
				{
					break;
				}

				var length = BinaryPrimitives.ReadInt32BigEndian(header);
				if (length < 0 || length > _maxFrameBytes)
				{
					_logger.LogWarning(
						"Gate connection {ConnectionId} received a frame with invalid length {Length} (limit {MaxFrameBytes}); closing the connection.",
						ConnectionId, length, _maxFrameBytes);
					((IDisposable)_client).Dispose();
					break;
				}

				var payload = new byte[length];
				if (length > 0 && !await ReadExactAsync(stream, payload).ConfigureAwait(false))
				{
					break;
				}

				FrameReceived?.Invoke(this, new GateFrameReceivedEventArgs(payload));
			}
		}
		catch (Exception ex)
		{
			RaiseDisconnected(ex);
			return;
		}

		RaiseDisconnected(null);
	}

	private void RaiseDisconnected(Exception? reason)
	{
		EventHandler<GateDisconnectedEventArgs>? handlers;
		lock (_sync)
		{
			_isConnected = false;
			if (_closeRaised)
			{
				return;
			}

			_closeRaised = true;
			handlers = Disconnected;
		}

		handlers?.Invoke(this, new GateDisconnectedEventArgs(reason));
	}

	private async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer[total..]).ConfigureAwait(false);
			if (read == 0)
			{
				return false;
			}

			total += read;
		}

		return true;
	}
}
