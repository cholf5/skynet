using System.Buffers.Binary;
using System.Threading.Channels;

namespace Skynet.Cluster;

/// <summary>
/// Identifies the well-known frame types of the Skynet cluster framing protocol
/// (<c>[type:1][length:4 big-endian][payload]</c>). The numeric values mirror the wire encoding
/// shared by <c>TcpTransport</c> and <c>KcpTransport</c>.
/// </summary>
public enum FaultInjectionFrameType : byte
{
	/// <summary>The initial node-id exchange frame of a new connection.</summary>
	Handshake = 1,

	/// <summary>An actor envelope (request or response).</summary>
	Envelope = 2,

	/// <summary>A keep-alive frame emitted by the heartbeat loop.</summary>
	Heartbeat = 3
}

/// <summary>
/// Describes a single assembled cluster frame observed by <see cref="FaultInjectingStream"/>, so a
/// <see cref="FaultInjectionOptions.FrameSelector"/> can restrict faults to a subset of traffic.
/// </summary>
/// <param name="Type">The raw frame type byte from the wire.</param>
/// <param name="PayloadLength">The announced payload length in bytes, excluding the 5-byte header.</param>
public readonly record struct FaultInjectionFrame(byte Type, int PayloadLength)
{
	/// <summary>
	/// Gets the well-known frame type of the frame. Only meaningful when <see cref="Type"/> is one
	/// of the three cluster frame types; extension frame types surface their raw byte cast.
	/// </summary>
	public FaultInjectionFrameType FrameType => (FaultInjectionFrameType)Type;
}

/// <summary>
/// Configuration for <see cref="FaultInjectingStream"/>. Every random decision is drawn from a
/// single <see cref="Random"/> seeded with <see cref="Seed"/>, so a given sequence of written
/// frames always produces the same fault pattern (no wall-clock input feeds the decisions).
/// </summary>
public sealed class FaultInjectionOptions
{
	/// <summary>
	/// Gets or sets the seed of the random source that draws drop decisions and delay samples.
	/// The same seed over the same frame sequence reproduces the identical fault pattern.
	/// </summary>
	public int Seed
	{
		get;
		init;
	} = 20260915;

	/// <summary>
	/// Gets or sets the probability that an affected frame is silently dropped, in <c>[0, 1]</c>.
	/// Drops are frame-atomic: a dropped frame vanishes completely, so the remaining byte stream
	/// stays aligned and the connection survives. Defaults to 0 (no loss).
	/// </summary>
	public double DropRate
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets the inclusive lower bound of the uniform write-delay distribution applied to
	/// affected frames. Defaults to <see cref="TimeSpan.Zero"/>.
	/// </summary>
	public TimeSpan MinWriteDelay
	{
		get;
		init;
	} = TimeSpan.Zero;

	/// <summary>
	/// Gets or sets the inclusive upper bound of the uniform write-delay distribution applied to
	/// affected frames. Must be greater than or equal to <see cref="MinWriteDelay"/>. Equal bounds
	/// produce a fixed delay; <see cref="TimeSpan.Zero"/> for both disables delaying.
	/// </summary>
	public TimeSpan MaxWriteDelay
	{
		get;
		init;
	} = TimeSpan.Zero;

	/// <summary>
	/// Gets or sets an optional predicate deciding which frames are affected by drop and delay
	/// faults. Frames for which the predicate returns <see langword="false"/> are forwarded
	/// immediately and consume no randomness — exclude <see cref="FaultInjectionFrameType.Handshake"/>
	/// and <see cref="FaultInjectionFrameType.Heartbeat"/> frames to keep control traffic healthy
	/// while data traffic is degraded. When <see langword="null"/>, every frame is affected.
	/// </summary>
	public Func<FaultInjectionFrame, bool>? FrameSelector
	{
		get;
		init;
	}

	/// <summary>
	/// Gets or sets the largest frame payload the stream assembles before deciding its fate. A
	/// header announcing a larger payload is treated as a protocol violation: the link is broken
	/// and pending writes fail. Defaults to 16 MB, matching the default frame guard of
	/// <c>TcpTransport</c>.
	/// </summary>
	public int MaxFrameBytes
	{
		get;
		init;
	} = 16 * 1024 * 1024;
}

/// <summary>
/// A stream decorator that injects reproducible transport faults into a cluster connection:
/// frame-atomic packet loss, write delay (fixed or uniformly distributed), a manually triggered
/// disconnect (<see cref="BreakConnection"/>), and a half-open link (<see cref="StallReads"/>,
/// the local side stops consuming incoming data without closing the connection).
/// <para>
/// Written bytes are reassembled into whole protocol frames; drop and delay decisions happen per
/// frame, so a fault can never tear a frame in half and corrupt the byte stream. Delayed frames
/// are released in write order (a frame never overtakes an earlier one), preserving the ordered
/// delivery semantics of the underlying stream. All decisions come from the seeded
/// <see cref="FaultInjectionOptions.Seed"/> source, making chaos scenarios reproducible.
/// </para>
/// <para>
/// The class is intended for tests: exactly one task writes and one task reads at a time (the
/// pattern <c>TcpTransport</c> uses per connection), while <see cref="StallReads"/>,
/// <see cref="ResumeReads"/>, and <see cref="BreakConnection"/> are thread-safe. Write completion
/// means the frame was accepted for delivery — mirroring TCP buffering — not that it reached the
/// inner stream; a dropped frame is silently swallowed exactly like a lost packet.
/// </para>
/// </summary>
public sealed class FaultInjectingStream : Stream
{
	private const int FrameHeaderLength = 5;

	private readonly Stream _inner;
	private readonly FaultInjectionOptions _options;
	private readonly Random _random;
	private readonly object _stateLock = new();
	private readonly Channel<OutgoingFrame> _outgoing = Channel.CreateUnbounded<OutgoingFrame>(
		new UnboundedChannelOptions { SingleReader = true });
	private readonly List<TaskCompletionSource<bool>> _stalledReaders = [];
	private byte[] _assembly = [];
	private int _assemblyLength;
	private long _lastDueTimestamp;
	private Task? _pumpTask;
	private bool _readsStalled;
	private bool _broken;
	private bool _disposed;
	private int _selectedFrames;
	private int _droppedFrames;
	private int _delayedFrames;
	private int _forwardedFrames;

	/// <summary>
	/// Initializes a new instance of the <see cref="FaultInjectingStream"/> class wrapping the
	/// specified stream.
	/// </summary>
	/// <param name="inner">The stream whose traffic is degraded (for example a <c>NetworkStream</c>
	/// or the <c>SslStream</c> above it).</param>
	/// <param name="options">The fault configuration; <see langword="null"/> applies defaults,
	/// which inject nothing until <see cref="StallReads"/> or <see cref="BreakConnection"/> is used.</param>
	public FaultInjectingStream(Stream inner, FaultInjectionOptions? options = null)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
		_options = options ?? new FaultInjectionOptions();
		if (_options.DropRate is < 0 or > 1)
		{
			throw new ArgumentOutOfRangeException(nameof(options), _options.DropRate,
				"DropRate must be within [0, 1].");
		}

		if (_options.MinWriteDelay < TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(options), _options.MinWriteDelay,
				"MinWriteDelay must not be negative.");
		}

		if (_options.MaxWriteDelay < _options.MinWriteDelay)
		{
			throw new ArgumentOutOfRangeException(nameof(options), _options.MaxWriteDelay,
				"MaxWriteDelay must be greater than or equal to MinWriteDelay.");
		}

		if (_options.MaxFrameBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), _options.MaxFrameBytes,
				"MaxFrameBytes must be positive.");
		}

		_random = new Random(_options.Seed);
	}

	/// <summary>
	/// Gets the number of frames selected by the fault policy (before any drop/delay decision).
	/// Frames rejected by <see cref="FaultInjectionOptions.FrameSelector"/> are not counted.
	/// </summary>
	public int SelectedFrameCount => Volatile.Read(ref _selectedFrames);

	/// <summary>Gets the number of frames silently dropped so far.</summary>
	public int DroppedFrameCount => Volatile.Read(ref _droppedFrames);

	/// <summary>Gets the number of frames held back by a non-zero write delay so far.</summary>
	public int DelayedFrameCount => Volatile.Read(ref _delayedFrames);

	/// <summary>Gets the number of frames written to the inner stream so far.</summary>
	public int ForwardedFrameCount => Volatile.Read(ref _forwardedFrames);

	/// <summary>Gets a value indicating whether the link has been broken (see <see cref="BreakConnection"/>).</summary>
	public bool IsBroken
	{
		get
		{
			lock (_stateLock)
			{
				return _broken;
			}
		}
	}

	/// <inheritdoc />
	public override bool CanRead => _inner.CanRead;

	/// <inheritdoc />
	public override bool CanWrite => _inner.CanWrite;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	/// <inheritdoc />
	public override void Flush() => _inner.Flush();

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (buffer.Length - offset < count)
		{
			throw new ArgumentException("The buffer is too small for the requested byte count.");
		}

		// Synchronous reads bypass the half-open stall (the cluster transport only performs
		// asynchronous I/O); a disposed or broken inner stream fails on its own.
		return _inner.Read(buffer, offset, count);
	}

	/// <inheritdoc />
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		while (true)
		{
			TaskCompletionSource<bool> release;
			lock (_stateLock)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_broken)
				{
					throw new IOException("The simulated connection has failed; no further reads are possible.");
				}

				if (!_readsStalled)
				{
					break;
				}

				release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				_stalledReaders.Add(release);
			}

			try
			{
				// A stalled read waits until the link is resumed, broken, disposed, or the caller
				// cancels — the exact observable behavior of a socket read on a half-open link.
				await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				lock (_stateLock)
				{
					_stalledReaders.Remove(release);
				}
			}
		}

		return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (buffer.Length - offset < count)
		{
			throw new ArgumentException("The buffer is too small for the requested byte count.");
		}

		WriteCore(buffer.AsSpan(offset, count));
	}

	/// <inheritdoc />
	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		WriteCore(buffer.Span);
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void SetLength(long value) => throw new NotSupportedException();

	/// <summary>
	/// Puts the link into a half-open state: reads no longer complete, but the connection stays
	/// open and writes keep flowing. The local side stops receiving any frames (heartbeats
	/// included), so its dead-link detection eventually declares the peer dead — the scenario
	/// <see cref="StallReads"/> exists to reproduce. Idempotent.
	/// </summary>
	public void StallReads()
	{
		lock (_stateLock)
		{
			_readsStalled = true;
		}
	}

	/// <summary>
	/// Ends a half-open state started by <see cref="StallReads"/>; every pending read completes
	/// and subsequent reads consume the data buffered on the inner stream. Idempotent.
	/// </summary>
	public void ResumeReads()
	{
		lock (_stateLock)
		{
			_readsStalled = false;
			WakeStalledReadersLocked();
		}
	}

	/// <summary>
	/// Simulates an abrupt link failure: pending and subsequent reads fail, subsequent writes
	/// fail with an <see cref="IOException"/>, and the inner stream is disposed so the peer
	/// observes the disconnect immediately. Queued frames that were not yet written are lost,
	/// like in-flight bytes on a dead socket. Idempotent.
	/// </summary>
	public void BreakConnection()
	{
		lock (_stateLock)
		{
			BreakCoreLocked();
		}

		_inner.Dispose();
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		lock (_stateLock)
		{
			if (!_disposed)
			{
				_disposed = true;
				_outgoing.Writer.TryComplete();
				WakeStalledReadersLocked();
			}
		}

		_inner.Dispose();
		base.Dispose(disposing);
	}

	private void WriteCore(ReadOnlySpan<byte> chunk)
	{
		lock (_stateLock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_broken)
			{
				throw new IOException("The simulated connection has failed; no further writes are accepted.");
			}

			try
			{
				AssembleAndDispatch(chunk);
				if (_pumpTask is null)
				{
					_pumpTask = Task.Run(PumpAsync);
				}
			}
			catch (IOException)
			{
				// A frame length violation means the byte stream is misaligned beyond repair; tear
				// the link down so readers (and the peer) observe a failure instead of garbage.
				BreakCoreLocked();
				_inner.Dispose();
				throw;
			}
		}
	}

	/// <summary>
	/// Appends the written bytes to the partial-frame assembly buffer and dispatches every frame
	/// that became complete. Called under <see cref="_stateLock"/>; the write path is serialized
	/// by the transport's write lock.
	/// </summary>
	private void AssembleAndDispatch(ReadOnlySpan<byte> chunk)
	{
		EnsureAssemblyCapacity(_assemblyLength + chunk.Length);
		chunk.CopyTo(_assembly.AsSpan(_assemblyLength));
		_assemblyLength += chunk.Length;

		var now = Environment.TickCount64;
		var consumed = 0;
		while (_assemblyLength - consumed >= FrameHeaderLength)
		{
			var payloadLength = BinaryPrimitives.ReadInt32BigEndian(_assembly.AsSpan(consumed + 1, 4));
			if (payloadLength < 0 || payloadLength > _options.MaxFrameBytes)
			{
				throw new IOException(
					$"Frame length {payloadLength} is invalid (limit {_options.MaxFrameBytes}); the stream is misaligned.");
			}

			var frameLength = FrameHeaderLength + payloadLength;
			if (_assemblyLength - consumed < frameLength)
			{
				break;
			}

			var frameBytes = _assembly.AsSpan(consumed, frameLength).ToArray();
			consumed += frameLength;
			DispatchFrame(frameBytes, now);
		}

		if (consumed > 0)
		{
			// Keep the trailing partial frame (if any) at the front of the assembly buffer.
			_assembly.AsSpan(consumed, _assemblyLength - consumed).CopyTo(_assembly);
			_assemblyLength -= consumed;
		}
	}

	private void DispatchFrame(byte[] frameBytes, long now)
	{
		var info = new FaultInjectionFrame(frameBytes[0], frameBytes.Length - FrameHeaderLength);
		if (_options.FrameSelector is { } selector && !selector(info))
		{
			EnqueueForDeliveryLocked(frameBytes, now);
			return;
		}

		Interlocked.Increment(ref _selectedFrames);
		if (_random.NextDouble() < _options.DropRate)
		{
			Interlocked.Increment(ref _droppedFrames);
			return;
		}

		var delayRange = (_options.MaxWriteDelay - _options.MinWriteDelay).TotalMilliseconds;
		var delay = _options.MinWriteDelay.TotalMilliseconds + (_random.NextDouble() * delayRange);
		if (delay > 0)
		{
			Interlocked.Increment(ref _delayedFrames);
		}

		EnqueueForDeliveryLocked(frameBytes, now + (long)delay);
	}

	private void EnqueueForDeliveryLocked(byte[] frameBytes, long dueTimestamp)
	{
		// Monotonic clamp: a frame can never overtake an earlier one, so the byte order written to
		// the inner stream always matches the order the frames were handed to this stream.
		if (dueTimestamp < _lastDueTimestamp)
		{
			dueTimestamp = _lastDueTimestamp;
		}

		_lastDueTimestamp = dueTimestamp;
		_outgoing.Writer.TryWrite(new OutgoingFrame(frameBytes, dueTimestamp));
	}

	private async Task PumpAsync()
	{
		try
		{
			while (await _outgoing.Reader.WaitToReadAsync().ConfigureAwait(false))
			{
				while (_outgoing.Reader.TryRead(out var frame))
				{
					var dueIn = (int)Math.Min(int.MaxValue - 1, frame.DueTimestamp - Environment.TickCount64);
					if (dueIn > 0)
					{
						await Task.Delay(dueIn).ConfigureAwait(false);
					}

					await _inner.WriteAsync(frame.Bytes, CancellationToken.None).ConfigureAwait(false);
					Interlocked.Increment(ref _forwardedFrames);
				}
			}
		}
		catch (Exception)
		{
			// The inner stream failed or was disposed mid-write: mark the link broken so writers
			// fail fast and stalled readers wake up, mirroring an unexpected disconnect.
			BreakConnection();
		}
	}

	private void BreakCoreLocked()
	{
		if (_broken)
		{
			return;
		}

		_broken = true;
		_outgoing.Writer.TryComplete();
		WakeStalledReadersLocked();
	}

	private void WakeStalledReadersLocked()
	{
		foreach (var waiter in _stalledReaders)
		{
			waiter.TrySetResult(true);
		}

		_stalledReaders.Clear();
	}

	private void EnsureAssemblyCapacity(int required)
	{
		if (required <= _assembly.Length)
		{
			return;
		}

		var capacity = Math.Max(_assembly.Length * 2, 256);
		while (capacity < required)
		{
			capacity *= 2;
		}

		Array.Resize(ref _assembly, capacity);
	}

	private readonly record struct OutgoingFrame(byte[] Bytes, long DueTimestamp);
}
