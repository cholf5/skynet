using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Skynet.Net.Tests;

/// <summary>
/// Transport-level tests for <see cref="TcpGateClientTransport"/> framing: concurrent sends must
/// not interleave frames, and the receive pump must reject oversized length prefixes.
/// </summary>
public sealed class TcpGateClientTransportTests
{
	private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

	[Fact]
	public async Task ConcurrentSendsReachServerAsIntactOrderedFrames()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();

		var transport = new TcpGateClientTransport();
		await using var connection = await transport.ConnectAsync(
			new GateEndpoint("Gate0", GetListenerAddress(listener)));
		using var serverClient = await listener.AcceptTcpClientAsync().WaitAsync(WaitTimeout);

		const int senderCount = 8;
		const int framesPerSender = 100;
		var frames = new ConcurrentQueue<byte[]>();
		var capture = CaptureFramesAsync(serverClient, frames, senderCount * framesPerSender);

		// Start every sender at the same instant so their writes genuinely overlap.
		var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var senders = Enumerable.Range(0, senderCount).Select(sender => Task.Run(async () =>
		{
			await startGate.Task.ConfigureAwait(false);
			for (var sequence = 0; sequence < framesPerSender; sequence++)
			{
				await connection.SendAsync(BuildFramePayload(sender, sequence)).ConfigureAwait(false);
			}
		})).ToArray();
		startGate.TrySetResult();
		await Task.WhenAll(senders).WaitAsync(WaitTimeout);
		await capture.WaitAsync(WaitTimeout);

		frames.Should().HaveCount(senderCount * framesPerSender);
		foreach (var group in frames.GroupBy(payload => payload[0]))
		{
			var sender = group.Key;
			var payloads = group.ToList();
			payloads.Should().HaveCount(framesPerSender, "sender {0} must have all frames delivered", sender);
			for (var sequence = 0; sequence < framesPerSender; sequence++)
			{
				var payload = payloads[sequence];
				payload.Length.Should().Be(BuildFramePayload(sender, sequence).Length,
					"sender {0} frame {1} must not be corrupted or interleaved", sender, sequence);
				payload[1].Should().Be((byte)sequence,
					"sender {0} frames must arrive in order", sender);
				for (var index = 2; index < payload.Length; index++)
				{
					payload[index].Should().Be((byte)(sender * 31 + sequence + index),
						"sender {0} frame {1} payload must be intact", sender, sequence);
				}
			}
		}
	}

	[Fact]
	public async Task OversizedFrameLengthIsRejectedAndConnectionCloses()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();

		var transport = new TcpGateClientTransport(new TcpGateClientTransportOptions { MaxFrameBytes = 1024 });
		await using var connection = await transport.ConnectAsync(
			new GateEndpoint("Gate0", GetListenerAddress(listener)));
		using var serverClient = await listener.AcceptTcpClientAsync().WaitAsync(WaitTimeout);

		var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		connection.Disconnected += (_, _) => disconnected.TrySetResult();

		// Malicious length header (int.MaxValue) sent while the server keeps the socket open;
		// the client must reject it before allocating and actively close the connection.
		var header = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(header, int.MaxValue);
		var serverStream = serverClient.GetStream();
		await serverStream.WriteAsync(header);

		await disconnected.Task.WaitAsync(WaitTimeout);
		connection.IsConnected.Should().BeFalse();

		var read = await serverStream.ReadAsync(new byte[1]).AsTask().WaitAsync(WaitTimeout);
		read.Should().Be(0, "the client must actively close the socket after rejecting the frame");
	}

	[Fact]
	public async Task SendsDuringConnectionReplacementFailGracefullyWithoutUnobservedExceptions()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();

		var transport = new TcpGateClientTransport();
		var client = new GateClient(
			new GateEndpoint("Gate0", GetListenerAddress(listener)),
			transport,
			new ReconnectPolicy(maxAttempts: 10, initialDelay: TimeSpan.Zero));

		await client.StartAsync();
		var firstServerClient = await listener.AcceptTcpClientAsync().WaitAsync(WaitTimeout);
		var firstConnectionId = client.Proxy.ConnectionId;
		DrainQuietly(firstServerClient);

		// Reconnect bookkeeping: the listener accepts the replacement connection in the background.
		var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(async () =>
		{
			var replacement = await listener.AcceptTcpClientAsync().WaitAsync(WaitTimeout);
			DrainQuietly(replacement);
			reconnected.TrySetResult();
		});

		var observedFailures = new ConcurrentBag<Exception>();
		var firstSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstFailureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var sawSuccessAfterReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var senders = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
		{
			while (!sawSuccessAfterReconnect.Task.IsCompleted)
			{
				try
				{
					await client.Proxy.SendAsync(new byte[16]).ConfigureAwait(false);
					firstSuccess.TrySetResult();
					if (reconnected.Task.IsCompleted)
					{
						sawSuccessAfterReconnect.TrySetResult();
					}
				}
				catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
				{
					// Expected while the old connection is dropped and swapped; nothing may escape.
					observedFailures.Add(ex);
					firstFailureObserved.TrySetResult();
					await Task.Delay(1).ConfigureAwait(false);
				}
			}
		})).ToArray();

		// Event-driven sequencing (no sleeps): wait until the senders are actually running, drop
		// the connection from the server side (disconnect -> dispose -> reconnect -> replace
		// while sends are in flight), wait until the failure path was exercised, then until the
		// client reconnected and a send succeeded on the replacement connection.
		await firstSuccess.Task.WaitAsync(WaitTimeout);
		firstServerClient.Dispose();
		await firstFailureObserved.Task.WaitAsync(WaitTimeout);
		await reconnected.Task.WaitAsync(WaitTimeout);
		await sawSuccessAfterReconnect.Task.WaitAsync(WaitTimeout);
		await Task.WhenAll(senders).WaitAsync(WaitTimeout);

		observedFailures.Should().NotBeEmpty("sends in flight during the swap must fail gracefully, not hang");
		observedFailures.Should().OnlyContain(ex =>
			ex is InvalidOperationException || ex is IOException || ex is ObjectDisposedException);
		client.Proxy.IsConnected.Should().BeTrue();
		client.Proxy.ConnectionId.Should().NotBe(firstConnectionId);

		await client.StopAsync();
	}

	[Fact]
	public async Task DisposeWakesSendersQueuedOnSendLock()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();

		var transport = new TcpGateClientTransport();
		await using var connection = await transport.ConnectAsync(
			new GateEndpoint("Gate0", GetListenerAddress(listener)));
		using var serverClient = await listener.AcceptTcpClientAsync().WaitAsync(WaitTimeout);

		// Regression for C-1: DisposeAsync used to dispose the semaphore itself, so a sender
		// queued on WaitAsync hung forever (the in-flight holder's Release threw
		// ObjectDisposedException and was swallowed). Hold the transport's send lock directly
		// (white-box) so a second sender deterministically queues on WaitAsync — the exact
		// state that used to deadlock — then dispose and mimic the in-flight holder's
		// finally-release. Sender B must wake and fail instead of hanging.
		var sendLock = (SemaphoreSlim)typeof(TcpGateProxyConnection)
			.GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(connection)!;
		await sendLock.WaitAsync();

		var senderB = connection.SendAsync(new byte[16]);
		senderB.IsCompleted.Should().BeFalse("sender B must be queued on the send lock");

		await connection.DisposeAsync();
		sendLock.Release();

		await Assert.ThrowsAnyAsync<Exception>(() => senderB.WaitAsync(WaitTimeout));
	}

	private static byte[] BuildFramePayload(int sender, int sequence)
	{
		var length = 2 + ((sender * 7 + sequence * 3) % 255);
		var payload = new byte[length];
		payload[0] = (byte)sender;
		payload[1] = (byte)sequence;
		for (var index = 2; index < length; index++)
		{
			payload[index] = (byte)(sender * 31 + sequence + index);
		}

		return payload;
	}

	private static string GetListenerAddress(TcpListener listener)
		=> ((IPEndPoint)listener.LocalEndpoint).ToString();

	private static async Task CaptureFramesAsync(TcpClient client, ConcurrentQueue<byte[]> frames, int count)
	{
		var stream = client.GetStream();
		var header = new byte[4];
		for (var i = 0; i < count; i++)
		{
			await ReadExactAsync(stream, header).ConfigureAwait(false);
			var length = BinaryPrimitives.ReadInt32BigEndian(header);
			var payload = new byte[length];
			if (length > 0)
			{
				await ReadExactAsync(stream, payload).ConfigureAwait(false);
			}

			frames.Enqueue(payload);
		}
	}

	private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer[total..]).ConfigureAwait(false);
			if (read == 0)
			{
				throw new IOException("unexpected EOF while reading a frame from the client.");
			}

			total += read;
		}
	}

	private static void DrainQuietly(TcpClient client)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				var buffer = new byte[1024];
				while (await client.GetStream().ReadAsync(buffer).ConfigureAwait(false) > 0)
				{
				}
			}
			catch
			{
				// Draining best-effort; the socket dies with the test.
			}
		});
	}
}
