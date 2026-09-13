using kcp2k;

namespace Skynet.Cluster.Transport.Kcp;

/// <summary>
/// Isolates the vendored kcp2k state machine behind a minimal wrapper so the rest of the
/// transport never touches kcp2k types directly. The wrapped <see cref="kcp2k.Kcp"/> instance
/// is not thread-safe; it is only ever touched by the owning <see cref="KcpConnectionPump"/>
/// processing loop.
/// </summary>
internal sealed class KcpSession
{
	private const int Mtu = 1200;
	private readonly kcp2k.Kcp _kcp;

	public KcpSession(uint conversationId, int intervalMilliseconds, bool noDelay, int fastResend,
		bool disableCongestionWindow, int sendWindow, int receiveWindow, Action<byte[], int> output)
	{
		_kcp = new kcp2k.Kcp(conversationId, output);
		_kcp.SetMtu(Mtu);
		_kcp.SetWindowSize((uint)sendWindow, (uint)receiveWindow);
		_kcp.SetNoDelay(noDelay ? 1u : 0u, (uint)intervalMilliseconds, fastResend, disableCongestionWindow);
	}

	/// <summary>Gets the number of segments waiting to be acknowledged or flushed.</summary>
	public int WaitSend => _kcp.WaitSnd;

	public void Send(byte[] data)
	{
		_kcp.Send(data, 0, data.Length);
	}

	public int Input(byte[] data)
	{
		return _kcp.Input(data, 0, data.Length);
	}

	/// <summary>
	/// Receives the next complete application message from the KCP receive queue, or
	/// <see langword="null"/> when no complete message is pending.
	/// </summary>
	public byte[]? TryReceive()
	{
		var size = _kcp.PeekSize();
		if (size <= 0)
		{
			return null;
		}

		var buffer = new byte[size];
		var received = _kcp.Receive(buffer, buffer.Length);
		return received <= 0 ? null : buffer;
	}

	public void Update(uint currentTimeMilliseconds)
	{
		_kcp.Update(currentTimeMilliseconds);
	}

	public void Flush()
	{
		_kcp.Flush();
	}
}
