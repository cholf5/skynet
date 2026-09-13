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
	private ISessionFrameCipher? _cipher;

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
	public bool IsReadyForBusiness => _handshake is null || _cipher is not null;

	/// <summary>Gets the activated frame cipher; null until <see cref="Activate"/> is called.</summary>
	public ISessionFrameCipher? Cipher => _cipher;

	/// <summary>Gets the handshake session; null in plaintext mode.</summary>
	public GateEncryptionServerSession? Handshake => _handshake;

	/// <summary>
	/// Processes one frame while the handshake is still pending. Returns the outbound response frame and/or
	/// the derived session key. Throws <see cref="GateHandshakeException"/> with a stable code on any
	/// protocol violation.
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
				return new GateHandshakeStep(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs), null);
			}
			case GateFrameType.HandshakeConfirmKey:
			{
				var sessionKey = _handshake!.ProcessConfirmEncryptKey(span[1..]);
				return new GateHandshakeStep(null, sessionKey);
			}
			default:
			{
				throw new GateHandshakeException(
					GateHandshakeErrors.BusinessFrameBeforeHandshake,
					$"Frame type 0x{span[0]:X2} received before the encryption handshake completed; business frames are rejected until the handshake finishes.");
			}
		}
	}

	/// <summary>Activates the session cipher with the derived session key (after authentication succeeded).</summary>
	public void Activate(byte[] sessionKey)
	{
		_cipher = SessionFrameCipherFactory.Create(_cipherId, sessionKey);
	}

	/// <summary>Builds the encrypted ConfirmEncryptKeyAck frame whose plaintext is the negotiated cipher id.</summary>
	public byte[] BuildConfirmAck()
	{
		return GateFrameCodec.EncryptFrame(_cipher!, [(byte)_handshake!.CipherId]);
	}

	/// <summary>
	/// Decrypts one post-handshake frame. Plaintext mode returns the payload unchanged; encrypted mode
	/// throws <see cref="CryptographicException"/> on malformed or inauthentic frames.
	/// </summary>
	public byte[] DecryptInbound(byte[] payload)
	{
		if (_cipher is null)
		{
			return payload;
		}

		return GateFrameCodec.DecryptFrame(_cipher, payload);
	}
}

/// <summary>
/// Wraps a session connection so every outbound payload is encrypted with the session cipher. Used as the
/// <see cref="ISessionConnection"/> handed to the session actor once the handshake completed.
/// </summary>
internal sealed class EncryptedSessionConnection(ISessionConnection inner, ISessionFrameCipher cipher) : ISessionConnection
{
	public System.Net.EndPoint? RemoteEndPoint => inner.RemoteEndPoint;

	public DateTimeOffset LastActivity => inner.LastActivity;

	public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
	{
		var frame = GateFrameCodec.EncryptFrame(cipher, payload.Span);
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
