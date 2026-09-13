using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>A token issued by the gate in response to a RequestEncryptToken frame.</summary>
public sealed record GateEncryptionToken(byte[] Token, long ExpireUnixMs);

/// <summary>
/// Gate-side per-connection handshake state. Owns the pending token issued for the connection and exposes
/// the two operations driven by the gate receive loop: <see cref="IssueToken"/> when the client requests a
/// token and <see cref="ProcessConfirmEncryptKey"/> when the client confirms the session key. Single-thread
/// by contract: the gate receive loop serializes inbound frames.
/// </summary>
public sealed class GateEncryptionServerSession
{
	private readonly IGateRsaKeyProvider _keyProvider;
	private readonly RSAEncryptionPadding _padding;
	private readonly TimeSpan _tokenLifetime;
	private readonly IGateHandshakeRandomSource _randomSource;
	private readonly byte[]? _hkdfSalt;
	private readonly byte _cipherId;
	private readonly Func<DateTimeOffset> _clock;
	private byte[]? _pendingToken;
	private long _pendingExpireUnixMs;
	private byte[]? _sessionKey;

	/// <summary>
	/// Creates a server handshake session. Defaults follow the modernized protocol: OAEP-SHA256 padding,
	/// a 30 second token lifetime, AES-256-GCM as the session cipher and the built-in HKDF salt.
	/// </summary>
	public GateEncryptionServerSession(
		IGateRsaKeyProvider keyProvider,
		RSAEncryptionPadding? padding = null,
		TimeSpan? tokenLifetime = null,
		IGateHandshakeRandomSource? randomSource = null,
		byte[]? hkdfSalt = null,
		byte cipherId = AesGcmSessionFrameCipher.DefaultCipherId,
		Func<DateTimeOffset>? clock = null)
	{
		_keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
		_padding = padding ?? RSAEncryptionPadding.OaepSHA256;
		var lifetime = tokenLifetime ?? TimeSpan.FromSeconds(30);
		if (lifetime <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(tokenLifetime), lifetime, "Token lifetime must be positive.");
		}

		_tokenLifetime = lifetime;
		_randomSource = randomSource ?? DefaultGateHandshakeRandomSource.Instance;
		_hkdfSalt = hkdfSalt;
		_cipherId = cipherId;
		_clock = clock ?? (static () => DateTimeOffset.UtcNow);
	}

	/// <summary>Gets the cipher id announced in the ConfirmEncryptKeyAck frame.</summary>
	public byte CipherId => _cipherId;

	/// <summary>Gets the session key after a successful <see cref="ProcessConfirmEncryptKey"/>; otherwise null.</summary>
	public byte[]? SessionKey => _sessionKey;

	/// <summary>Gets a value indicating whether the handshake has completed successfully.</summary>
	public bool IsCompleted => _sessionKey is not null;

	/// <summary>
	/// Mints a fresh token, remembers it and returns it for the ResponseEncryptToken frame. Throws
	/// <see cref="GateHandshakeException"/> with <see cref="GateHandshakeErrors.DuplicateHandshake"/> when
	/// the handshake already completed (second handshake on the same connection is rejected).
	/// </summary>
	public GateEncryptionToken IssueToken()
	{
		if (_sessionKey is not null)
		{
			throw new GateHandshakeException(GateHandshakeErrors.DuplicateHandshake, "Gate encryption handshake already completed for this connection.");
		}

		var now = _clock();
		var token = GateHandshakeCodec.MintTokenBytes(_randomSource);
		_pendingToken = token;
		_pendingExpireUnixMs = now.ToUnixTimeMilliseconds() + (long)_tokenLifetime.TotalMilliseconds;
		return new GateEncryptionToken(token, _pendingExpireUnixMs);
	}

	/// <summary>
	/// Decrypts an inbound ConfirmEncryptKey frame body (opaque RSA ciphertext), validates the echoed token
	/// against the token issued by <see cref="IssueToken"/>, derives the session key and returns it. Throws
	/// <see cref="GateHandshakeException"/> with a stable code on any validation failure so the gate can
	/// close the connection with a diagnosable error.
	/// </summary>
	public byte[] ProcessConfirmEncryptKey(ReadOnlySpan<byte> ciphertext)
	{
		if (_pendingToken is null)
		{
			throw new GateHandshakeException(GateHandshakeErrors.UnexpectedConfirm, "Gate ConfirmEncryptKey arrived before a token was issued.");
		}

		if (_clock().ToUnixTimeMilliseconds() > _pendingExpireUnixMs)
		{
			_pendingToken = null;
			throw new GateHandshakeException(GateHandshakeErrors.TokenExpired, "Gate encryption token has expired before ConfirmEncryptKey arrived.");
		}

		byte[] plaintext;
		try
		{
			using var privateKey = _keyProvider.CreatePrivateKey();
			plaintext = privateKey.Decrypt(ciphertext.ToArray(), _padding);
		}
		catch (CryptographicException ex)
		{
			throw new GateHandshakeException(GateHandshakeErrors.RsaDecryptFailed, "Gate ConfirmEncryptKey could not be RSA-decrypted: " + ex.Message);
		}

		byte[] seed;
		byte[] echoedToken;
		try
		{
			(seed, echoedToken) = GateHandshakeCodec.ParseRsaPlaintext(plaintext);
		}
		catch (GateHandshakeException ex)
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidPlaintext, ex.Message);
		}

		if (!CryptographicOperations.FixedTimeEquals(_pendingToken, echoedToken))
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidToken, "Gate ConfirmEncryptKey echoed a token that does not match the one issued for this connection.");
		}

		_sessionKey = GateHandshakeCodec.DeriveSessionKey(seed, _cipherId, _hkdfSalt);
		_pendingToken = null;
		return _sessionKey;
	}
}
