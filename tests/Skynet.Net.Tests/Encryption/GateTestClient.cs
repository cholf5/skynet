using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Skynet.Core;
using Skynet.Net.Encryption;

namespace Skynet.Net.Tests;

/// <summary>
/// Minimal TCP client that speaks the gate handshake protocol (RequestEncryptToken / ConfirmEncryptKey)
/// and exchanges AES-GCM encrypted business frames. Serves as the reference client for tests and the
/// future client SDK.
/// </summary>
internal sealed class GateTestClient(IPEndPoint endpoint, InMemoryGateRsaKeyProvider? keyProvider) : IAsyncDisposable
{
	private readonly TcpClient _tcp = new();
	private NetworkStream? _stream;
	private byte[]? _lastSentFrame;

	public GateEncryptionClientSession Session { get; private set; } = null!;

	public async Task ConnectAsync()
	{
		await _tcp.ConnectAsync(endpoint.Address, endpoint.Port).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		_stream = _tcp.GetStream();
	}

	/// <summary>
	/// Runs the full handshake: requests a token, derives the session key, sends the RSA-encrypted confirm
	/// payload and waits for the encrypted ack that completes the session. Throws
	/// <see cref="GateHandshakeException"/> when the gate answers with a HandshakeError frame.
	/// </summary>
	public async Task HandshakeAsync()
	{
		Session = new GateEncryptionClientSession(keyProvider!);

		await WriteRawAsync(GateEncryptionClientSession.CreateRequestTokenFrame()).ConfigureAwait(false);
		var tokenFrame = await ReadRawOrThrowAsync().ConfigureAwait(false);
		ThrowIfErrorFrame(tokenFrame);
		var confirm = Session.ProcessResponseToken(tokenFrame);
		await WriteRawAsync(confirm).ConfigureAwait(false);
		var ack = await ReadRawOrThrowAsync().ConfigureAwait(false);
		ThrowIfErrorFrame(ack);

		Session.ProcessConfirmAck(ack);
		await Session.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
	}

	private static void ThrowIfErrorFrame(byte[] frame)
	{
		var errorCode = GateFrameCodec.TryDecodeErrorFrame(frame);
		if (errorCode is not null)
		{
			throw new GateHandshakeException(errorCode, $"Gate rejected the handshake: {errorCode}.");
		}
	}

	/// <summary>Encrypts and sends a UTF-8 business payload. Remembers the raw encrypted frame for replay tests.</summary>
	public async Task SendBusinessAsync(string text)
	{
		var frame = Session.EncryptOutbound(Encoding.UTF8.GetBytes(text));
		_lastSentFrame = frame;
		await WriteRawAsync(frame).ConfigureAwait(false);
	}

	/// <summary>Resends the last raw encrypted frame byte-for-byte (replay simulation for tests).</summary>
	public async Task ResendLastFrameAsync()
	{
		if (_lastSentFrame is null)
		{
			throw new InvalidOperationException("No frame has been sent yet.");
		}

		await WriteRawAsync(_lastSentFrame).ConfigureAwait(false);
	}

	/// <summary>Receives and decrypts one business frame, returning it as UTF-8 text.</summary>
	public async Task<string> ReceiveBusinessAsync()
	{
		var raw = await ReadRawOrThrowAsync().ConfigureAwait(false);
		var plaintext = Session.DecryptInbound(raw);
		return Encoding.UTF8.GetString(plaintext);
	}

	public async Task WriteRawAsync(byte[] frame)
	{
		var header = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
		await _stream!.WriteAsync(header).ConfigureAwait(false);
		await _stream.WriteAsync(frame).ConfigureAwait(false);
		await _stream.FlushAsync().ConfigureAwait(false);
	}

	/// <summary>Reads one length-prefixed frame; returns null on clean EOF (connection closed).</summary>
	public async Task<byte[]?> ReadRawOrNullAsync()
	{
		var header = new byte[4];
		if (!await ReadExactlyAsync(header).ConfigureAwait(false))
		{
			return null;
		}

		var length = BinaryPrimitives.ReadInt32BigEndian(header);
		var payload = new byte[length];
		if (length > 0 && !await ReadExactlyAsync(payload).ConfigureAwait(false))
		{
			return null;
		}

		return payload;
	}

	private async Task<byte[]> ReadRawOrThrowAsync()
	{
		var frame = await ReadRawOrNullAsync().ConfigureAwait(false);
		return frame ?? throw new IOException("Connection closed while waiting for a frame.");
	}

	private async Task<bool> ReadExactlyAsync(Memory<byte> buffer)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = await _stream!.ReadAsync(buffer[total..]).ConfigureAwait(false);
			if (read == 0)
			{
				return false;
			}

			total += read;
		}

		return true;
	}

	public async ValueTask DisposeAsync()
	{
		_tcp.Dispose();
		await Task.CompletedTask.ConfigureAwait(false);
	}
}

internal sealed class EchoActor : Actor
{
	protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
	{
		return envelope.Payload switch
		{
			EchoRequest request => Task.FromResult<object?>(request.Text.ToUpperInvariant()),
			_ => throw new InvalidOperationException("Unexpected payload")
		};
	}
}

internal sealed record EchoRequest(string Text);

internal sealed class TestEchoRouter : ISessionMessageRouter
{
	private readonly ActorHandle _echo;

	public TestEchoRouter(ActorHandle echo)
	{
		_echo = echo;
	}

	public Task OnSessionStartedAsync(SessionContext context, CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	public async Task OnSessionMessageAsync(SessionContext context, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		var text = Encoding.UTF8.GetString(payload.Span);
		var reply = await context.CallAsync<string>(_echo, new EchoRequest(text), cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		await context.SendAsync(Encoding.UTF8.GetBytes(reply), cancellationToken).ConfigureAwait(false);
	}

	public Task OnSessionClosedAsync(SessionContext context, SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}
}
