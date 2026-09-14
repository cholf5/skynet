using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>
/// Per-connection state machine driving the gate side of the encryption handshake and the encrypted frame
/// boundary afterwards. In plaintext mode (no handshake session) every payload passes through unchanged.
/// In encrypted mode the gate must not hand any payload to the session actor before
/// <see cref="IsReadyForBusiness"/> turns true; non-handshake frames are rejected with
/// <see cref="GateHandshakeErrors.BusinessFrameBeforeHandshake"/>. Internal to the gate transport.
/// </summary>
internal sealed class GateSessionPipeline
{
	private readonly GateEncryptionServerSession? _handshake;
	private readonly byte _cipherId;
	private readonly GateReplayWindow _receiveWindow = new();
	private ISessionFrameCipher? _decryptCipher;
	private ISessionFrameCipher? _encryptCipher;
	private ulong _sendSequence;

	private GateSessionPipeline(GateEncryptionServerSession? handshake, byte cipherId)
	{
		_handshake = handshake;
		_cipherId = cipherId;
	}

	/// <summary>Creates a pass-through pipeline for the legacy plaintext mode.</summary>
	public static GateSessionPipeline CreatePlaintext()
	{
		return new GateSessionPipeline(null, 0);
	}

	/// <summary>Creates an encrypted pipeline bound to a per-connection handshake session.</summary>
	public static GateSessionPipeline CreateEncrypted(GateEncryptionServerSession handshake, byte cipherId)
	{
		ArgumentNullException.ThrowIfNull(handshake);
		return new GateSessionPipeline(handshake, cipherId);
	}

	/// <summary>Gets a value indicating whether the connection must complete the handshake before business frames.</summary>
	public bool RequiresHandshake => _handshake is not null;

	/// <summary>Gets a value indicating whether business frames may be processed.</summary>
	public bool IsReadyForBusiness => _handshake is null || _decryptCipher is not null;

	/// <summary>Gets the handshake session; null in plaintext mode.</summary>
	public GateEncryptionServerSession? Handshake => _handshake;

	/// <summary>
	/// Processes one frame while the handshake is still pending. Returns the outbound response frame and/or
	/// the derived directional session keys. Throws <see cref="GateHandshakeException"/> with a stable code
	/// on any protocol violation.
	/// </summary>
	public GateHandshakeStep ProcessHandshakeFrame(ReadOnlyMemory<byte> payload)
	{
		var span = payload.Span;
		if (span.Length < 1)
		{
			throw new GateHandshakeException(GateHandshakeErrors.UnknownFrame, "Gate handshake frame is empty.");
		}

		switch (span[0])
		{
			case GateFrameType.HandshakeRequestToken:
			{
				var token = _handshake!.IssueToken();
				return new GateHandshakeStep(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs), null, null);
			}
			case GateFrameType.HandshakeConfirmKey:
			{
				_handshake!.ProcessConfirmEncryptKey(span[1..]);
				return new GateHandshakeStep(null, _handshake.ClientToGateKey, _handshake.GateToClientKey);
			}
			default:
			{
				throw new GateHandshakeException(
					GateHandshakeErrors.BusinessFrameBeforeHandshake,
					$"Frame type 0x{span[0]:X2} received before the encryption handshake completed; business frames are rejected until the handshake finishes.");
			}
		}
	}

	/// <summary>
	/// Activates the directional session ciphers (after authentication succeeded): the client → gate key
	/// decrypts inbound frames, the gate → client key encrypts outbound frames.
	/// </summary>
	public void Activate(byte[] clientToGateKey, byte[] gateToClientKey)
	{
		_decryptCipher = SessionFrameCipherFactory.Create(_cipherId, clientToGateKey);
		_encryptCipher = SessionFrameCipherFactory.Create(_cipherId, gateToClientKey);
	}

	/// <summary>
	/// Encrypts one outbound frame with the gate → client key, stamping the next send sequence number.
	/// Also used for the ConfirmEncryptKeyAck, which is the first gate → client frame (sequence 0).
	/// </summary>
	public byte[] EncryptOutbound(ReadOnlySpan<byte> plaintext)
	{
		return GateFrameCodec.EncryptFrame(_encryptCipher!, plaintext, _sendSequence++);
	}

	/// <summary>Builds the encrypted ConfirmEncryptKeyAck frame whose plaintext is the negotiated cipher id.</summary>
	public byte[] BuildConfirmAck()
	{
		return EncryptOutbound([(byte)_handshake!.CipherId]);
	}

	/// <summary>
	/// Decrypts one post-handshake frame through the receive replay window. Plaintext mode returns the
	/// payload unchanged; encrypted mode throws <see cref="CryptographicException"/> on malformed frames,
	/// replays and inauthentic frames.
	/// </summary>
	public byte[] DecryptInbound(byte[] payload)
	{
		if (_decryptCipher is null)
		{
			return payload;
		}

		return GateFrameCodec.DecryptFrame(_decryptCipher, payload, _receiveWindow);
	}
}

/// <summary>
/// Wraps a session connection so every outbound payload is encrypted with the gate → client direction key
/// and stamped with the pipeline send sequence counter. Used as the <see cref="ISessionConnection"/> handed
/// to the session actor once the handshake completed. Single-thread by contract: the session actor
/// serializes sends, and the ack (sent by the receive loop before the actor processes any message) already
/// consumed sequence 0.
/// </summary>
internal sealed class EncryptedSessionConnection(ISessionConnection inner, GateSessionPipeline pipeline) : ISessionConnection
{
	public System.Net.EndPoint? RemoteEndPoint => inner.RemoteEndPoint;

	public DateTimeOffset LastActivity => inner.LastActivity;

	public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		var frame = pipeline.EncryptOutbound(payload.Span);
		await inner.SendAsync(frame, cancellationToken).ConfigureAwait(false);
	}

	public ValueTask CloseAsync(SessionCloseReason reason, string? description, CancellationToken cancellationToken)
	{
		return inner.CloseAsync(reason, description, cancellationToken);
	}

	public void MarkActivity()
	{
		inner.MarkActivity();
	}

	public ValueTask DisposeAsync()
	{
		return inner.DisposeAsync();
	}
}
