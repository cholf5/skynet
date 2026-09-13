using System.Threading.Channels;

namespace Skynet.Cluster.Transport.Kcp;

/// <summary>
/// Serializes all KCP state-machine work for one connection onto a single driving loop.
/// </summary>
/// <remarks>
/// kcp2k's <c>Kcp</c> is not thread-safe, and the reliable ordered semantics of the KCP stream
/// depend on <c>Send</c>/<c>Input</c>/<c>Update</c> observing state in a consistent order. Rather
/// than synchronizing individual calls, every operation (application send, inbound datagram,
/// periodic tick) is funneled through a channel consumed by exactly one processing task
/// (<c>SingleReader</c>). The processing task is the only caller of the session and the only
/// consumer of its output datagrams, which mirrors the "single-thread pump" threading model of the
/// protocol-independent reliable layer. Callers never block on the KCP state machine itself; they
/// only enqueue operations.
/// </remarks>
internal sealed class KcpConnectionPump : IAsyncDisposable
{
	private readonly KcpSession _session;
	private readonly Channel<PumpOperation> _operations = Channel.CreateUnbounded<PumpOperation>(
		new UnboundedChannelOptions
		{
			SingleReader = true,
			SingleWriter = false
		});
	private readonly Queue<byte[]> _pendingOutput = new();
	private readonly Action<byte[]> _sendDatagram;
	private readonly Action<byte[]> _onMessage;
	private readonly Action<Exception> _onFault;
	private readonly CancellationTokenSource _cts = new();
	private Task? _processingTask;
	private Task? _timerTask;
	private int _started;

	public KcpConnectionPump(KcpSession session, TimeSpan updateInterval, Action<byte[]> sendDatagram,
		Action<byte[]> onMessage, Action<Exception> onFault)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_sendDatagram = sendDatagram ?? throw new ArgumentNullException(nameof(sendDatagram));
		_onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
		_onFault = onFault ?? throw new ArgumentNullException(nameof(onFault));
		if (updateInterval <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(updateInterval), updateInterval,
				"The update interval must be positive.");
		}

		UpdateInterval = updateInterval;
	}

	private TimeSpan UpdateInterval { get; }

	/// <summary>Gets the number of KCP segments queued for transmission or awaiting acknowledgement.</summary>
	public int WaitSend => _session.WaitSend;

	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) != 0)
		{
			return;
		}

		_processingTask = Task.Run(ProcessOperationsAsync);
		_timerTask = Task.Run(TimerLoopAsync);
	}

	/// <summary>Queues an application message for transmission over the KCP stream.</summary>
	public ValueTask SendAsync(byte[] data, CancellationToken cancellationToken)
	{
		return _operations.Writer.WriteAsync(PumpOperation.Send(data), cancellationToken);
	}

	/// <summary>Feeds a raw UDP datagram received from the peer into the KCP state machine.</summary>
	public ValueTask InputAsync(byte[] datagram, CancellationToken cancellationToken)
	{
		return _operations.Writer.WriteAsync(PumpOperation.Input(datagram), cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		if (!_cts.IsCancellationRequested)
		{
			await _cts.CancelAsync().ConfigureAwait(false);
		}

		_operations.Writer.TryComplete();
		foreach (var task in new[] { _timerTask, _processingTask })
		{
			if (task is null)
			{
				continue;
			}

			try
			{
				await task.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_cts.Dispose();
	}

	private async Task TimerLoopAsync()
	{
		using var timer = new PeriodicTimer(UpdateInterval);
		try
		{
			while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
			{
				await _operations.Writer.WriteAsync(PumpOperation.Tick(), _cts.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (ChannelClosedException)
		{
		}
	}

	private async Task ProcessOperationsAsync()
	{
		try
		{
			await foreach (var operation in _operations.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
			{
				switch (operation.Type)
				{
					case PumpOperationType.Send:
						_session.Send(operation.Data);
						_session.Flush();
						break;
					case PumpOperationType.Input:
						_session.Input(operation.Data);
						break;
					case PumpOperationType.Tick:
						_session.Update(UncheckedCurrentMilliseconds());
						break;
				}

				DrainOutput();
				DrainMessages();
			}
		}
		catch (OperationCanceledException) when (_cts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			if (!_cts.IsCancellationRequested)
			{
				await _cts.CancelAsync().ConfigureAwait(false);
				_onFault(ex);
			}
		}
	}

	private void DrainOutput()
	{
		while (_pendingOutput.TryDequeue(out var datagram))
		{
			_sendDatagram(datagram);
		}
	}

	private void DrainMessages()
	{
		while (true)
		{
			var message = _session.TryReceive();
			if (message is null)
			{
				return;
			}

			_onMessage(message);
		}
	}

	private static uint UncheckedCurrentMilliseconds()
	{
		return unchecked((uint)Environment.TickCount64);
	}

	private readonly record struct PumpOperation(PumpOperationType Type, byte[] Data)
	{
		public static PumpOperation Send(byte[] data) => new(PumpOperationType.Send, data);

		public static PumpOperation Input(byte[] data) => new(PumpOperationType.Input, data);

		public static PumpOperation Tick() => new(PumpOperationType.Tick, []);
	}

	private enum PumpOperationType
	{
		Send,
		Input,
		Tick
	}
}
