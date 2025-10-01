using System.Buffers.Binary;
using System.Net.Sockets;

namespace Skynet.Net;

internal sealed class TcpSessionConnection(TcpClient client) : ISessionConnection
{
	private readonly NetworkStream _stream = client.GetStream();
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
	private bool _disposed;

	public System.Net.EndPoint? RemoteEndPoint => client.Client.RemoteEndPoint;

	public DateTimeOffset LastActivity => new DateTimeOffset(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

	public void MarkActivity()
	{
		_lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
	}

	public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var header = new byte[4];
			BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
			await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
			if (!payload.IsEmpty)
			{
				await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
			}
			await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
			client.Client.Shutdown(SocketShutdown.Both);
		}
		catch (Exception)
		{
			// ignored
		}
		finally
		{
			client.Close();
		}
		await Task.CompletedTask.ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await _stream.DisposeAsync().ConfigureAwait(false);
		client.Dispose();
		_sendLock.Dispose();
	}
}
