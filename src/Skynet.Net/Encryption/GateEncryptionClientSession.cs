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
	private byte[]? _sessionKey;
	private ISessionFrameCipher? _frameCipher;

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

	/// <summary>Gets the session key derived from the seed; null until <see cref="ProcessResponseToken"/> ran.</summary>
	public byte[]? SessionKey => _sessionKey;

	/// <summary>
	/// Gets the token expiry (unix milliseconds, as announced by the gate) decoded from the
	/// ResponseEncryptToken frame; null before that frame arrived. Informational only: the client
	/// clock is not authoritative, so expiry is enforced by the gate — never reject the handshake
	/// based on this value.
	/// </summary>
	public long? TokenExpireUnixMs { get; private set; }

	/// <summary>Gets the frame cipher for business frames; null until the handshake completed.</summary>
	public ISessionFrameCipher? FrameCipher => _frameCipher;

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
		if (_sessionKey is not null)
		{
			throw new GateHandshakeException(GateHandshakeErrors.DuplicateResponse, "Gate ResponseEncryptToken received more than once for the same connection.");
		}

		var token = GateFrameCodec.DecodeResponseToken(frame);
		TokenExpireUnixMs = token.ExpireUnixMs;
		var seed = _randomSource.GetSeedBytes(GateHandshakeCodec.SeedByteLength);
		_sessionKey = GateHandshakeCodec.DeriveSessionKey(seed, _cipherId, _hkdfSalt);

		byte[] ciphertext;
		try
		{
			var plaintext = GateHandshakeCodec.BuildRsaPlaintext(seed, token.Token, _randomSource);
			using var publicKey = _keyProvider.CreatePublicKey();
			ciphertext = publicKey.Encrypt(plaintext, _padding);
		}
		catch (CryptographicException ex)
		{
			_sessionKey = null;
			throw new GateHandshakeException(GateHandshakeErrors.RsaEncryptFailed, "Gate ConfirmEncryptKey could not be RSA-encrypted: " + ex.Message);
		}

		return GateFrameCodec.EncodeConfirmKey(ciphertext);
	}

	/// <summary>
	/// Consumes the inbound encrypted ConfirmEncryptKeyAck frame. Successful decryption confirms the gate
	/// derived the same session key and completes <see cref="Completion"/>; <see cref="FrameCipher"/> becomes
	/// available for business frames afterwards.
	/// </summary>
	public void ProcessConfirmAck(ReadOnlySpan<byte> frame)
	{
		if (_sessionKey is null)
		{
			Fail(new GateHandshakeException(GateHandshakeErrors.AckWithoutKey, "Gate ConfirmEncryptKeyAck arrived before a session key was derived."));
			return;
		}

		var cipher = _frameCipher ??= SessionFrameCipherFactory.Create(_cipherId, _sessionKey);
		byte[] plaintext;
		try
		{
			plaintext = GateFrameCodec.DecryptFrame(cipher, frame);
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
}
