namespace Skynet.Cluster.Transport.Reliable;

/// <summary>
/// Provides a protocol-independent sliding-window layer that delivers messages reliably and in
/// order over an unreliable packet channel (typically UDP datagrams). The queue is ported from a
/// proven internal implementation with two deliberate fixes over the original:
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread model:</b> the queue is deliberately lock-free and not thread-safe. <see cref="Send"/>,
/// <see cref="Input"/>, and <see cref="Update"/> must all be invoked serially from a single driving
/// context (a dedicated pump loop or actor). Callers that own a socket or actor loop interleave
/// those calls between IO completions; nothing inside the queue requires additional synchronization.
/// </para>
/// <para>
/// <b>Fix 1 — dead-link terminal state:</b> the original kept unacknowledged segments in the send
/// queue after their retransmission budget (<see cref="DefaultDeadLink"/> xmits) was exhausted and
/// re-invoked the broken callback on every subsequent <see cref="Update"/> tick. Once any segment
/// exhausts its budget the queue now transitions to a <c>Broken</c> terminal state: pending
/// segments are dropped, <see cref="IsBroken"/> reports <see langword="true"/>, the callback fires
/// exactly once, and further <see cref="Update"/>/<see cref="Input"/> calls are ignored until
/// <see cref="Reset"/> restores the queue.
/// </para>
/// <para>
/// <b>Fix 2 — adaptive RTO:</b> the original used a fixed initial RTO with exponential backoff for
/// every segment. Acknowledgements for segments that were never retransmitted (Karn's algorithm)
/// now feed an RFC 6298 style estimator (SRTT/RTTVAR smoothing) and <see cref="CurrentRto"/> is
/// clamped to <c>[<paramref name="minRto"/>, <see cref="MaxRto"/>]</c>. Timeout retransmissions
/// still double the segment's RTO; fast-resend triggered retransmissions do not inflate it.
/// </para>
/// </remarks>
public sealed class ReliableQueue
{
	public const ushort DefaultWindowSize = 32;
	public static readonly TimeSpan DefaultRto = TimeSpan.FromSeconds(2);
	public const ushort DefaultFastResend = 3;
	public const ushort DefaultDeadLink = 20;
	public static readonly TimeSpan MaxRto = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan DefaultMinRto = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan RttGranularity = TimeSpan.FromMilliseconds(10);
	private const double RttAlpha = 1.0 / 8.0;
	private const double RttBeta = 1.0 / 4.0;

	private readonly LinkedList<ReliableSegment> _sendQueue = [];
	private readonly Queue<ReliableSegment> _sendBuffer = [];
	private readonly SortedDictionary<ushort, ReliablePacket> _recvBuffer = [];
	private readonly Action<ReliablePacket> _sendPacket;
	private readonly Action<byte[], byte[]> _onReceive;
	private readonly Action? _onBroken;
	private readonly ushort _windowSize;
	private readonly ushort _fastResend;
	private readonly ushort _deadLink;
	private readonly TimeSpan _initialRto;
	private readonly TimeSpan _minRto;

	private ushort _sendNext;
	private ushort _recvNext;
	private DateTimeOffset _lastNow = DateTimeOffset.UnixEpoch;
	private QueueState _state = QueueState.Active;
	private double _srttMilliseconds;
	private double _rttvarMilliseconds;
	private TimeSpan _currentRto;

	public ReliableQueue(
		Action<ReliablePacket> sendPacket,
		Action<byte[], byte[]> onReceive,
		Action? onBroken = null,
		ushort windowSize = DefaultWindowSize,
		TimeSpan? initialRto = null,
		ushort fastResend = DefaultFastResend,
		ushort deadLink = DefaultDeadLink,
		TimeSpan? minRto = null)
	{
		_sendPacket = sendPacket ?? throw new ArgumentNullException(nameof(sendPacket));
		_onReceive = onReceive ?? throw new ArgumentNullException(nameof(onReceive));
		_onBroken = onBroken;
		_windowSize = windowSize == 0 ? throw new ArgumentOutOfRangeException(nameof(windowSize)) : windowSize;
		_initialRto = initialRto ?? DefaultRto;
		_fastResend = fastResend;
		_deadLink = deadLink;
		_minRto = minRto ?? DefaultMinRto;
		_currentRto = _initialRto;
	}

	/// <summary>Gets the number of segments that have been transmitted but not yet acknowledged.</summary>
	public int SendQueueCount => _sendQueue.Count;

	/// <summary>Gets the number of messages held back because the send window is full.</summary>
	public int SendBufferCount => _sendBuffer.Count;

	/// <summary>Gets the number of out-of-order packets buffered while waiting for a gap to fill.</summary>
	public int RecvBufferCount => _recvBuffer.Count;

	/// <summary>Gets the sequence number assigned to the next outgoing message.</summary>
	public ushort SendNext => _sendNext;

	/// <summary>Gets the sequence number expected from the peer for the next in-order delivery.</summary>
	public ushort RecvNext => _recvNext;

	/// <summary>
	/// Gets a value indicating whether the queue has entered the dead-link terminal state. A broken
	/// queue performs no work and drops all input until <see cref="Reset"/> is called.
	/// </summary>
	public bool IsBroken => _state == QueueState.Broken;

	/// <summary>
	/// Gets the current adaptive retransmission timeout derived from RTT samples (RFC 6298 style),
	/// clamped to <c>[<paramref name="minRto"/>, <see cref="MaxRto"/>]</c>.
	/// </summary>
	public TimeSpan CurrentRto => _currentRto;

	/// <summary>
	/// Queues a message for reliable, ordered delivery to the peer. Throws
	/// <see cref="InvalidOperationException"/> when the queue is broken.
	/// </summary>
	public void Send(ReadOnlyMemory<byte> message, ReadOnlyMemory<byte> context = default, DateTimeOffset? now = null)
	{
		ThrowIfBroken();
		var current = now ?? _lastNow;
		var segment = new ReliableSegment
		{
			Sn = _sendNext++,
			Una = _recvNext,
			Context = context.ToArray(),
			Message = message.ToArray(),
			Rto = _currentRto,
			ResendAtUtc = current + _currentRto
		};

		if (_sendQueue.Count < _windowSize)
		{
			SendSegment(segment, current, isInitialSend: true);
		}
		else
		{
			_sendBuffer.Enqueue(segment);
		}
	}

	/// <summary>
	/// Feeds a packet received from the peer into the queue. Packets are ignored while the queue is
	/// broken.
	/// </summary>
	public void Input(ReliablePacket packet, DateTimeOffset? now = null)
	{
		if (_state == QueueState.Broken)
		{
			return;
		}

		_lastNow = now ?? _lastNow;
		if (packet.Type == ReliablePacketType.Ack)
		{
			ProcessAck(packet.Sn, packet.Una, _lastNow);
			FlushSendBuffer(_lastNow);
			return;
		}

		if (SeqLess(packet.Sn, _recvNext))
		{
			// A retransmission of data this side already delivered: re-acknowledge it so the
			// peer's retransmission timer stops, but never deliver it a second time.
			_sendPacket(ReliablePacket.Ack(packet.Sn, _recvNext));
			return;
		}

		if (packet.Sn == _recvNext)
		{
			_sendPacket(ReliablePacket.Ack(packet.Sn, _recvNext));
			Deliver(packet);
			while (_recvBuffer.Remove(_recvNext, out var next))
			{
				Deliver(next);
			}

			return;
		}

		if ((ushort)(packet.Sn - _recvNext) >= _windowSize)
		{
			// Beyond the receive window: drop WITHOUT acknowledging. Acknowledging data that is
			// not buffered would let the peer retire the segment and permanently lose the message
			// whenever the window stalls behind a gap.
			return;
		}

		_recvBuffer.TryAdd(packet.Sn, packet);
		_sendPacket(ReliablePacket.Ack(packet.Sn, _recvNext));
	}

	/// <summary>
	/// Advances retransmission timers. Must be called periodically by the driving pump; the call is
	/// a no-op once the queue is broken.
	/// </summary>
	public void Update(DateTimeOffset now)
	{
		if (_state == QueueState.Broken)
		{
			return;
		}

		_lastNow = now;
		foreach (var segment in _sendQueue.ToArray())
		{
			if (segment.Xmit >= _deadLink)
			{
				EnterBrokenState();
				return;
			}

			var timedOut = now >= segment.ResendAtUtc;
			var fastResend = segment.FastAck >= _fastResend;
			if (!timedOut && !fastResend)
			{
				continue;
			}

			if (timedOut)
			{
				segment.Rto = TimeSpan.FromMilliseconds(Math.Min(segment.Rto.TotalMilliseconds * 2, MaxRto.TotalMilliseconds));
			}

			SendSegment(segment, now, isInitialSend: false);
			segment.FastAck = 0;
		}
	}

	/// <summary>
	/// Restores a broken (or live) queue to a pristine state so it can be reused for a new exchange:
	/// all pending state is dropped, sequence numbers and the RTT estimator restart from zero.
	/// </summary>
	public void Reset()
	{
		_sendQueue.Clear();
		_sendBuffer.Clear();
		_recvBuffer.Clear();
		_sendNext = 0;
		_recvNext = 0;
		_lastNow = DateTimeOffset.UnixEpoch;
		_state = QueueState.Active;
		_srttMilliseconds = 0;
		_rttvarMilliseconds = 0;
		_currentRto = _initialRto;
	}

	private void ProcessAck(ushort sn, ushort una, DateTimeOffset now)
	{
		for (var node = _sendQueue.First; node is not null;)
		{
			var next = node.Next;
			var segment = node.Value;
			if (segment.Sn == sn || SeqLess(segment.Sn, una))
			{
				_sendQueue.Remove(node);
				if (segment.Xmit == 1)
				{
					// Karn's algorithm: only segments transmitted exactly once yield RTT samples.
					SampleRtt(now - segment.LastSendAtUtc);
				}
			}
			else if (SeqLess(segment.Sn, sn))
			{
				segment.FastAck++;
			}

			node = next;
		}
	}

	private void FlushSendBuffer(DateTimeOffset now)
	{
		while (_sendQueue.Count < _windowSize && _sendBuffer.TryDequeue(out var segment))
		{
			SendSegment(segment, now, isInitialSend: true);
		}
	}

	private void SendSegment(ReliableSegment segment, DateTimeOffset now, bool isInitialSend)
	{
		segment.Una = _recvNext;
		segment.Xmit++;
		segment.LastSendAtUtc = now;
		segment.ResendAtUtc = now + segment.Rto;
		if (isInitialSend)
		{
			_sendQueue.AddLast(segment);
		}

		_sendPacket(segment.ToPacket());
	}

	private void Deliver(ReliablePacket packet)
	{
		_onReceive(packet.Context, packet.Message);
		_recvNext++;
	}

	private void SampleRtt(TimeSpan rtt)
	{
		if (rtt < TimeSpan.Zero)
		{
			rtt = TimeSpan.Zero;
		}

		var sample = rtt.TotalMilliseconds;
		if (_srttMilliseconds <= 0)
		{
			_srttMilliseconds = sample;
			_rttvarMilliseconds = sample / 2;
		}
		else
		{
			_rttvarMilliseconds = (1 - RttBeta) * _rttvarMilliseconds +
				RttBeta * Math.Abs(_srttMilliseconds - sample);
			_srttMilliseconds = (1 - RttAlpha) * _srttMilliseconds + RttAlpha * sample;
		}

		var scaledRto = _srttMilliseconds +
			Math.Max(RttGranularity.TotalMilliseconds, 4 * _rttvarMilliseconds);
		_currentRto = TimeSpan.FromMilliseconds(Math.Clamp(scaledRto, _minRto.TotalMilliseconds, MaxRto.TotalMilliseconds));
	}

	private void EnterBrokenState()
	{
		_state = QueueState.Broken;
		_sendQueue.Clear();
		_sendBuffer.Clear();
		_recvBuffer.Clear();
		_onBroken?.Invoke();
	}

	private void ThrowIfBroken()
	{
		if (_state == QueueState.Broken)
		{
			throw new InvalidOperationException(
				"The reliable queue is broken: the dead-link retransmission budget was exhausted. Reset the queue before reusing it.");
		}
	}

	private static bool SeqLess(ushort left, ushort right)
	{
		return (short)(left - right) < 0;
	}

	private enum QueueState
	{
		Active,
		Broken
	}
}
