using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>
/// Client-side per-connection handshake state. Generates the 16-byte seed, RSA-encrypts the padded confirm
/// payload with the gate public key and exposes a <see cref="Completion"/> task that finishes once the gate
/// sends the encrypted ConfirmEncryptKeyAck frame (decrypting the ack also proves the gate derived the same
/// session key). Single-thread by contract: the owning transport serializes inbound frames.
/// </summary>
public sealed class GateEncryptionClientSession
{
	private readonly IGateRsaKeyProvider _keyProvider;
	private readonly RSAEncryptionPadding _padding;
	private readonly IGateHandshakeRandomSource _randomSource;
	private readonly byte[]? _hkdfSalt;
	private readonly byte _cipherId;
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly GateReplayWindow _receiveWindow = new();
	private byte[]? _clientToGateKey;
	private byte[]? _gateToClientKey;
	private ISessionFrameCipher? _sendCipher;
	private ISessionFrameCipher? _receiveCipher;
	private ulong _sendSequence;

	/// <summary>
	/// Creates a client handshake session. Defaults follow the modernized protocol: OAEP-SHA256 padding,
	/// AES-256-GCM as the session cipher and the built-in HKDF salt. The salt and cipher id must match the
	/// gate configuration or the ack decryption fails.
	/// </summary>
	public GateEncryptionClientSession(
		IGateRsaKeyProvider keyProvider,
		RSAEncryptionPadding? padding = null,
		IGateHandshakeRandomSource? randomSource = null,
		byte[]? hkdfSalt = null,
		byte cipherId = AesGcmSessionFrameCipher.DefaultCipherId)
	{
		_keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
		_padding = padding ?? RSAEncryptionPadding.OaepSHA256;
		_randomSource = randomSource ?? DefaultGateHandshakeRandomSource.Instance;
		_hkdfSalt = hkdfSalt;
		_cipherId = cipherId;
	}

	/// <summary>
	/// Gets the token expiry (unix milliseconds, as announced by the gate) decoded from the
	/// ResponseEncryptToken frame; null before that frame arrived. Informational only: the client
	/// clock is not authoritative, so expiry is enforced by the gate — never reject the handshake
	/// based on this value.
	/// </summary>
	public long? TokenExpireUnixMs { get; private set; }

	/// <summary>Gets the client → gate key derived from the seed; null until <see cref="ProcessResponseToken"/> ran.</summary>
	public byte[]? ClientToGateKey => _clientToGateKey;

	/// <summary>Gets the gate → client key derived from the seed; null until <see cref="ProcessResponseToken"/> ran.</summary>
	public byte[]? GateToClientKey => _gateToClientKey;

	/// <summary>
	/// Gets the cipher used to encrypt outbound (client → gate) business frames; null until the handshake
	/// completed. Prefer the <see cref="EncryptOutbound"/>/<see cref="DecryptInbound"/> helpers, which also
	/// maintain the frame sequence numbers and the replay window.
	/// </summary>
	public ISessionFrameCipher? FrameCipher => _sendCipher;

	/// <summary>Gets a value indicating whether the handshake completed successfully.</summary>
	public bool IsCompleted => _completion.Task.Status == TaskStatus.RanToCompletion;

	/// <summary>Completes once the ConfirmEncryptKeyAck arrives (or faults when <see cref="Fail"/> is called).</summary>
	public Task Completion => _completion.Task;

	/// <summary>Encodes the outbound RequestEncryptToken frame.</summary>
	public static byte[] CreateRequestTokenFrame()
	{
		return GateFrameCodec.EncodeRequestToken();
	}

	/// <summary>
	/// Consumes an inbound ResponseEncryptToken frame: generates a fresh seed, derives the session key and
	/// returns the outbound ConfirmEncryptKey frame (RSA-encrypted). Throws
	/// <see cref="GateHandshakeException"/> on duplicate responses or malformed frames.
	/// </summary>
	public byte[] ProcessResponseToken(ReadOnlySpan<byte> frame)
	{
		if (_clientToGateKey is not null)
		{
			throw new GateHandshakeException(GateHandshakeErrors.DuplicateResponse, "Gate ResponseEncryptToken received more than once for the same connection.");
		}

		var token = GateFrameCodec.DecodeResponseToken(frame);
		TokenExpireUnixMs = token.ExpireUnixMs;
		var seed = _randomSource.GetSeedBytes(GateHandshakeCodec.SeedByteLength);
		_clientToGateKey = GateHandshakeCodec.DeriveSessionKey(seed, _cipherId, _hkdfSalt, GateKeyDirection.ClientToGate);
		_gateToClientKey = GateHandshakeCodec.DeriveSessionKey(seed, _cipherId, _hkdfSalt, GateKeyDirection.GateToClient);
		_sendCipher = SessionFrameCipherFactory.Create(_cipherId, _clientToGateKey);
		_receiveCipher = SessionFrameCipherFactory.Create(_cipherId, _gateToClientKey);

		byte[] ciphertext;
		try
		{
			var plaintext = GateHandshakeCodec.BuildRsaPlaintext(seed, token.Token, _randomSource);
			using var publicKey = _keyProvider.CreatePublicKey();
			ciphertext = publicKey.Encrypt(plaintext, _padding);
		}
		catch (CryptographicException ex)
		{
			// The handshake is unrecoverable; release the AEAD instances created before the failure.
			_sendCipher?.Dispose();
			_receiveCipher?.Dispose();
			_sendCipher = null;
			_receiveCipher = null;
			_clientToGateKey = null;
			_gateToClientKey = null;
			throw new GateHandshakeException(GateHandshakeErrors.RsaEncryptFailed, "Gate ConfirmEncryptKey could not be RSA-encrypted: " + ex.Message);
		}

		return GateFrameCodec.EncodeConfirmKey(ciphertext);
	}

	/// <summary>
	/// Consumes the inbound encrypted ConfirmEncryptKeyAck frame. Successful decryption confirms the gate
	/// derived the same directional keys and completes <see cref="Completion"/>; <see cref="FrameCipher"/>
	/// becomes available for business frames afterwards. The ack is the first gate → client frame and goes
	/// through the receive replay window.
	/// </summary>
	public void ProcessConfirmAck(ReadOnlySpan<byte> frame)
	{
		if (_clientToGateKey is null)
		{
			Fail(new GateHandshakeException(GateHandshakeErrors.AckWithoutKey, "Gate ConfirmEncryptKeyAck arrived before a session key was derived."));
			return;
		}

		byte[] plaintext;
		try
		{
			plaintext = GateFrameCodec.DecryptFrame(_receiveCipher!, frame, _receiveWindow);
		}
		catch (CryptographicException ex)
		{
			Fail(new GateHandshakeException(GateHandshakeErrors.AckDecryptFailed, "Gate ConfirmEncryptKeyAck could not be decrypted: " + ex.Message));
			return;
		}

		if (plaintext.Length != 1 || plaintext[0] != _cipherId)
		{
			Fail(new GateHandshakeException(GateHandshakeErrors.UnsupportedCipher, $"Gate negotiated cipher id does not match the client configuration (0x{_cipherId:X2})."));
			return;
		}

		_completion.TrySetResult();
	}

	/// <summary>Aborts the handshake with the given exception; wakes any awaiter of <see cref="Completion"/>.</summary>
	public void Fail(Exception? exception)
	{
		_completion.TrySetException(exception ?? new InvalidOperationException("Gate handshake aborted."));
	}

	/// <summary>
	/// Encrypts one outbound business frame with the client → gate key, stamping the next send sequence
	/// number. Throws <see cref="InvalidOperationException"/> when the handshake has not completed.
	/// </summary>
	public byte[] EncryptOutbound(ReadOnlySpan<byte> plaintext)
	{
		if (_sendCipher is null)
		{
			throw new InvalidOperationException("The encryption handshake has not completed; business frames cannot be sent yet.");
		}

		var frame = GateFrameCodec.EncryptFrame(_sendCipher, plaintext, _sendSequence++);
		return frame;
	}

	/// <summary>
	/// Decrypts one inbound (gate → client) business frame with the gate → client key through the receive
	/// replay window. Throws <see cref="CryptographicException"/> when the frame is malformed, a replay, or
	/// fails authentication.
	/// </summary>
	public byte[] DecryptInbound(byte[] frame)
	{
		if (_receiveCipher is null)
		{
			throw new InvalidOperationException("The encryption handshake has not completed; business frames cannot be received yet.");
		}

		return GateFrameCodec.DecryptFrame(_receiveCipher, frame, _receiveWindow);
	}
}
