using System.Buffers.Binary;
using System.Net.Sockets;

namespace Skynet.Net;

/// <summary>
/// Real TCP implementation of <see cref="IGateClientTransport"/>. Parses "host:port" addresses
/// and frames every payload as <c>[4-byte big-endian length][payload]</c> — the same framing the
/// inbound <see cref="GateServer"/> uses on its TCP channel, so the pool can talk to a Skynet
/// GateServer directly.
/// </summary>
public sealed class TcpGateClientTransport : IGateClientTransport
{
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

		return new TcpGateProxyConnection(endpoint, client, startReceiveLoop: true);
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
/// </summary>
public sealed class TcpGateProxyConnection : IGateProxyConnection
{
	private readonly TcpClient _client;
	private readonly object _sync = new();
	private bool _isConnected = true;
	private bool _closeRaised;

	internal TcpGateProxyConnection(GateEndpoint endpoint, TcpClient client, bool startReceiveLoop)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		_client = client ?? throw new ArgumentNullException(nameof(client));
		ConnectionId = $"{endpoint.Name}:{Guid.NewGuid():N}";
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

		var header = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
		var stream = _client.GetStream();
		await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
		if (payload.Length > 0)
		{
			await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
		}

		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		bool alreadyDisposed;
		lock (_sync)
		{
			alreadyDisposed = !_isConnected;
			_isConnected = false;
		}

		if (alreadyDisposed)
		{
			return;
		}

		((IDisposable)_client).Dispose();
		await Task.CompletedTask.ConfigureAwait(false);
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
				if (length < 0)
				{
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
