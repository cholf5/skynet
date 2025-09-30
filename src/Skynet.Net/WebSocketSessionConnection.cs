using System.Net.WebSockets;
using System.Threading;

namespace Skynet.Net;

internal sealed class WebSocketSessionConnection : ISessionConnection
{
	private readonly WebSocket _socket;
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private long _lastActivityTicks;
	private bool _disposed;

	public WebSocketSessionConnection(WebSocket socket)
	{
		_socket = socket;
		_lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
	}

	public System.Net.EndPoint? RemoteEndPoint => null;

	public DateTimeOffset LastActivity => new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

	public void MarkActivity()
	{
		_lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
	}

	public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await _socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
			MarkActivity();
		}
		finally
		{
			_sendLock.Release();
		}
	}

	public async ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, description, cancellationToken).ConfigureAwait(false);
		}
		catch
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
		await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disposed", CancellationToken.None).ConfigureAwait(false);
		_socket.Dispose();
		_sendLock.Dispose();
	}
}
