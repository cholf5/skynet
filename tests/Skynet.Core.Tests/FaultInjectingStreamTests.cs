using System.Buffers.Binary;
using FluentAssertions;
using Skynet.Cluster;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="FaultInjectingStream"/>: frame reassembly, seeded loss/delay
/// determinism, order preservation, the half-open stall, and manual disconnects. All fault
/// decisions are verified against fixed seeds, never against wall-clock timing.
/// </summary>
public sealed class FaultInjectingStreamTests
{
	[Fact]
	public async Task WriteAsync_ShouldReassembleAndForwardFramesWithoutFaults()
	{
		// Default options inject nothing: every written frame must reach the inner stream intact,
		// even when the caller splits a frame across several write calls.
		var inner = new RecordingStream();
		await using var stream = new FaultInjectingStream(inner);

		var envelope = Frame(FaultInjectionFrameType.Envelope, marker: 7);
		await stream.WriteAsync(envelope.AsMemory(0, 2));
		await stream.WriteAsync(envelope.AsMemory(2));
		var heartbeat = Frame(FaultInjectionFrameType.Heartbeat, marker: 9);
		await stream.WriteAsync(heartbeat);

		var written = await inner.WaitForWritesAsync(2, TimeSpan.FromSeconds(10));
		written.Should().HaveCount(2, "reassembled frames are forwarded as single writes");
		written[0].Should().Equal(envelope);
		written[1].Should().Equal(heartbeat);
		stream.SelectedFrameCount.Should().Be(2);
		stream.ForwardedFrameCount.Should().Be(2);
		stream.DroppedFrameCount.Should().Be(0);
		stream.DelayedFrameCount.Should().Be(0);
	}

	[Fact]
	public async Task FullDrop_ShouldDropSelectedFramesAndForwardExcludedOnes()
	{
		var inner = new RecordingStream();
		var options = new FaultInjectionOptions
		{
			DropRate = 1.0,
			FrameSelector = frame => frame.FrameType == FaultInjectionFrameType.Envelope
		};
		await using var stream = new FaultInjectingStream(inner, options);

		await stream.WriteAsync(Frame(FaultInjectionFrameType.Handshake, 1));
		await stream.WriteAsync(Frame(FaultInjectionFrameType.Envelope, 2));
		await stream.WriteAsync(Frame(FaultInjectionFrameType.Heartbeat, 3));
		await stream.WriteAsync(Frame(FaultInjectionFrameType.Envelope, 4));

		// Only the two excluded frames ever reach the inner stream.
		var written = await inner.WaitForWritesAsync(2, TimeSpan.FromSeconds(10));
		stream.SelectedFrameCount.Should().Be(2, "excluded frames consume no fault decision");
		stream.DroppedFrameCount.Should().Be(2);
		stream.ForwardedFrameCount.Should().Be(2, "handshake and heartbeat pass through untouched");
		written.Should().ContainSingle(chunk => chunk[0] == (byte)FaultInjectionFrameType.Handshake);
		written.Should().ContainSingle(chunk => chunk[0] == (byte)FaultInjectionFrameType.Heartbeat);
		written.Should().NotContain(chunk => chunk[0] == (byte)FaultInjectionFrameType.Envelope);
	}

	[Fact]
	public async Task SeededDropRate_ShouldReproduceIdenticalFaultPattern()
	{
		var first = await RunLossySequence(seed: 123, dropRate: 0.5);
		var second = await RunLossySequence(seed: 123, dropRate: 0.5);

		first.Forwarded.Should().Equal(second.Forwarded, "the same seed must reproduce the same delivered bytes");
		first.Dropped.Should().Be(second.Dropped);
		first.Dropped.Should().BeGreaterThan(0, "a 50% drop rate over 20 frames must drop some");
		first.Dropped.Should().BeLessThan(20, "a 50% drop rate over 20 frames must deliver some");
	}

	[Fact]
	public async Task WriteDelay_ShouldReleaseFramesInWriteOrder()
	{
		var inner = new RecordingStream();
		var options = new FaultInjectionOptions
		{
			MinWriteDelay = TimeSpan.FromMilliseconds(20),
			MaxWriteDelay = TimeSpan.FromMilliseconds(40)
		};
		await using var stream = new FaultInjectingStream(inner, options);

		for (byte marker = 0; marker < 6; marker++)
		{
			await stream.WriteAsync(Frame(FaultInjectionFrameType.Envelope, marker));
		}

		var written = await inner.WaitForWritesAsync(6, TimeSpan.FromSeconds(10));
		await WaitForQuiescenceAsync(stream, TimeSpan.FromSeconds(10));
		written.Select(chunk => (int)chunk[5]).Should().Equal(new[] { 0, 1, 2, 3, 4, 5 },
			"a delayed frame must never overtake an earlier one");
		stream.DelayedFrameCount.Should().Be(6);
		stream.DroppedFrameCount.Should().Be(0);
	}

	[Fact]
	public async Task StallReads_ShouldHoldReadsUntilResume()
	{
		var inner = new RecordingStream();
		await using var stream = new FaultInjectingStream(inner);
		inner.FeedRead([1, 2, 3]);
		var memory = new byte[16];

		stream.StallReads();
		var readTask = stream.ReadAsync(memory.AsMemory()).AsTask();
		var completed = await Task.WhenAny(readTask, Task.Delay(300));
		completed.Should().NotBeSameAs(readTask, "a stalled read must not complete while the link is half-open");

		stream.ResumeReads();
		var read = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
		read.Should().Be(3);
		memory.AsSpan(0, 3).ToArray().Should().Equal(1, 2, 3);
	}

	[Fact]
	public async Task StalledRead_ShouldHonorCancellation()
	{
		var inner = new RecordingStream();
		await using var stream = new FaultInjectingStream(inner);
		stream.StallReads();

		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
		var readTask = stream.ReadAsync(new byte[16].AsMemory(), cts.Token).AsTask();
		Func<Task> act = async () => await readTask;
		await act.Should().ThrowAsync<OperationCanceledException>().WaitAsync(TimeSpan.FromSeconds(5));

		// The canceled waiter must not leak: resuming and reading still works afterwards.
		inner.FeedRead([4]);
		stream.ResumeReads();
		var memory = new byte[4];
		(await stream.ReadAsync(memory.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
	}

	[Fact]
	public async Task BreakConnection_ShouldFailWritesWakeReadersAndDisposeInner()
	{
		var inner = new RecordingStream();
		await using var stream = new FaultInjectingStream(inner);
		stream.StallReads();
		var stalledRead = stream.ReadAsync(new byte[4].AsMemory()).AsTask();

		stream.BreakConnection();

		Func<Task> read = async () => await stalledRead;
		await read.Should().ThrowAsync<IOException>().WaitAsync(TimeSpan.FromSeconds(5));
		Func<Task> write = async () => await stream.WriteAsync(new byte[] { 1 });
		await write.Should().ThrowAsync<IOException>("writes on a broken link must fail fast");
		inner.IsDisposed.Should().BeTrue("breaking the link must close the inner stream so the peer notices");
		stream.IsBroken.Should().BeTrue();
	}

	[Fact]
	public async Task InvalidFrameLength_ShouldBreakConnection()
	{
		var inner = new RecordingStream();
		var options = new FaultInjectionOptions { MaxFrameBytes = 16 };
		await using var stream = new FaultInjectingStream(inner, options);

		var header = new byte[5];
		header[0] = (byte)FaultInjectionFrameType.Envelope;
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), 100);
		Action write = () => stream.WriteAsync(header);
		write.Should().Throw<IOException>("a frame beyond MaxFrameBytes is a protocol violation");
		stream.IsBroken.Should().BeTrue();
		inner.IsDisposed.Should().BeTrue();
	}

	[Theory]
	[InlineData(-0.1)]
	[InlineData(1.5)]
	public void Constructor_ShouldRejectDropRateOutsideUnitInterval(double dropRate)
	{
		var options = new FaultInjectionOptions { DropRate = dropRate };
		var create = () => new FaultInjectingStream(new RecordingStream(), options);
		create.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void Constructor_ShouldRejectInvertedDelayBounds()
	{
		var options = new FaultInjectionOptions
		{
			MinWriteDelay = TimeSpan.FromSeconds(2),
			MaxWriteDelay = TimeSpan.FromSeconds(1)
		};
		var create = () => new FaultInjectingStream(new RecordingStream(), options);
		create.Should().Throw<ArgumentOutOfRangeException>();
	}

	private static async Task<(byte[] Forwarded, int Dropped)> RunLossySequence(int seed, double dropRate)
	{
		var inner = new RecordingStream();
		await using var stream = new FaultInjectingStream(inner, new FaultInjectionOptions
		{
			Seed = seed,
			DropRate = dropRate,
			FrameSelector = frame => frame.FrameType == FaultInjectionFrameType.Envelope
		});

		for (byte marker = 0; marker < 20; marker++)
		{
			await stream.WriteAsync(Frame(FaultInjectionFrameType.Envelope, marker));
		}

		await WaitForQuiescenceAsync(stream, TimeSpan.FromSeconds(10));
		return (inner.WrittenChunks.SelectMany(chunk => chunk).ToArray(), stream.DroppedFrameCount);
	}

	private static async Task WaitForQuiescenceAsync(FaultInjectingStream stream, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline)
		{
			// Quiescent when every selected frame is accounted for: forwarded (with zero delay
			// configured) or dropped.
			if (stream.ForwardedFrameCount >= stream.SelectedFrameCount - stream.DroppedFrameCount)
			{
				return;
			}

			await Task.Delay(10);
		}

		throw new TimeoutException("The fault stream did not quiesce within the allotted time.");
	}

	private static byte[] Frame(FaultInjectionFrameType type, byte marker)
	{
		var frame = new byte[6];
		frame[0] = (byte)type;
		BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), 1);
		frame[5] = marker;
		return frame;
	}

	/// <summary>
	/// A minimal in-memory stream double: it records every written chunk in order and serves reads
	/// from data pushed in by <see cref="FeedRead"/>, so tests drive both directions explicitly.
	/// </summary>
	private sealed class RecordingStream : Stream
	{
		private readonly object _gate = new();
		private readonly List<byte[]> _written = [];
		private readonly Queue<byte[]> _pendingReads = [];
		private readonly Queue<TaskCompletionSource<byte[]>> _readWaiters = [];
		private bool _disposed;

		public override bool CanRead => !_disposed;
		public override bool CanWrite => !_disposed;
		public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public bool IsDisposed
		{
			get
			{
				lock (_gate)
				{
					return _disposed;
				}
			}
		}

		public IReadOnlyList<byte[]> WrittenChunks
		{
			get
			{
				lock (_gate)
				{
					return _written.ToArray();
				}
			}
		}

		/// <summary>Waits until at least <paramref name="count"/> chunks have been written.</summary>
		public async Task<IReadOnlyList<byte[]>> WaitForWritesAsync(int count, TimeSpan timeout)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (true)
			{
				var snapshot = WrittenChunks;
				if (snapshot.Count >= count)
				{
					return snapshot;
				}

				if (DateTime.UtcNow >= deadline)
				{
					throw new TimeoutException($"Only {snapshot.Count} of {count} expected writes arrived.");
				}

				await Task.Delay(10);
			}
		}

		public override void Flush()
		{
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_written.Add(buffer.ToArray());
			}

			return ValueTask.CompletedTask;
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_written.Add(buffer[offset..(offset + count)]);
			}
		}

		public void FeedRead(byte[] data)
		{
			lock (_gate)
			{
				if (_readWaiters.Count > 0)
				{
					_readWaiters.Dequeue().TrySetResult(data);
					return;
				}

				_pendingReads.Enqueue(data);
			}
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			byte[]? data;
			TaskCompletionSource<byte[]>? waiter = null;
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_pendingReads.Count > 0)
				{
					data = _pendingReads.Dequeue();
				}
				else
				{
					data = null;
					waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
					_readWaiters.Enqueue(waiter);
				}
			}

			if (data is null)
			{
				data = await waiter!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			}

			var copied = Math.Min(buffer.Length, data.Length);
			data.AsSpan(0, copied).CopyTo(buffer.Span);
			return copied;
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			lock (_gate)
			{
				_disposed = true;
			}

			base.Dispose(disposing);
		}
	}
}
