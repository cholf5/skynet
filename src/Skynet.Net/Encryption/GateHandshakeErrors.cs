namespace Skynet.Net.Encryption;

/// <summary>
/// Stable string error codes carried by <see cref="GateHandshakeException"/> and by the
/// <c>HandshakeError</c> wire frame (frame type 0x05). Clients can branch on these codes without
/// parsing exception messages.
/// </summary>
public static class GateHandshakeErrors
{
	/// <summary>A ConfirmEncryptKey frame arrived before the gate issued a token.</summary>
	public const string UnexpectedConfirm = "GateHandshakeUnexpectedConfirm";

	/// <summary>The issued token expired before the ConfirmEncryptKey frame arrived.</summary>
	public const string TokenExpired = "GateHandshakeTokenExpired";

	/// <summary>The echoed token inside the RSA-encrypted confirm payload does not match the issued token.</summary>
	public const string InvalidToken = "GateHandshakeInvalidToken";

	/// <summary>The RSA ciphertext of a ConfirmEncryptKey frame could not be decrypted.</summary>
	public const string RsaDecryptFailed = "GateHandshakeRsaDecryptFailed";

	/// <summary>The RSA plaintext of a ConfirmEncryptKey frame does not follow the wire layout.</summary>
	public const string InvalidPlaintext = "GateHandshakeInvalidPlaintext";

	/// <summary>The RSA encryption of a ConfirmEncryptKey payload failed on the client side.</summary>
	public const string RsaEncryptFailed = "GateHandshakeRsaEncryptFailed";

	/// <summary>The gate issued a second token for a connection that already completed the handshake.</summary>
	public const string DuplicateHandshake = "GateHandshakeDuplicateHandshake";

	/// <summary>The client received a second ResponseEncryptToken frame on the same connection.</summary>
	public const string DuplicateResponse = "GateHandshakeDuplicateResponse";

	/// <summary>The client received a ConfirmEncryptKeyAck before deriving a session key.</summary>
	public const string AckWithoutKey = "GateHandshakeAckWithoutKey";

	/// <summary>The client could not decrypt the ConfirmEncryptKeyAck frame.</summary>
	public const string AckDecryptFailed = "GateHandshakeAckDecryptFailed";

	/// <summary>The negotiated cipher id is not supported by this endpoint.</summary>
	public const string UnsupportedCipher = "GateHandshakeUnsupportedCipher";

	/// <summary>The handshake did not complete within the configured timeout.</summary>
	public const string Timeout = "GateHandshakeTimeout";

	/// <summary>A business frame arrived before the handshake completed.</summary>
	public const string BusinessFrameBeforeHandshake = "GateHandshakeBusinessFrameRejected";

	/// <summary>The frame received during the handshake phase is not a recognized handshake frame.</summary>
	public const string UnknownFrame = "GateHandshakeUnknownFrame";

	/// <summary>The authentication callback rejected the session.</summary>
	public const string AuthRejected = "GateHandshakeAuthRejected";

	/// <summary>A post-handshake frame could not be decrypted with the session key.</summary>
	public const string SessionDecryptFailed = "GateSessionDecryptFailed";
}

/// <summary>
/// Thrown by the handshake state machine when a step fails validation. The <see cref="Code"/> property
/// carries one of the stable codes from <see cref="GateHandshakeErrors"/>.
/// </summary>
public sealed class GateHandshakeException : Exception
{
	/// <summary>Gets the stable machine-readable error code.</summary>
	public string Code { get; }

	public GateHandshakeException(string code, string message)
		: base(message)
	{
		Code = code;
	}
}
