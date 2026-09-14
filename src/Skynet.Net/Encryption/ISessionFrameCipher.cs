using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>
/// Pluggable symmetric cipher used to encrypt session frames after the handshake completes. The handshake
/// layer depends only on this interface, so alternative ciphers (for example ChaCha20-Poly1305) can be
/// added without touching the handshake state machine. Implementations must be single-thread-safe per
/// session: the gate serializes frame encryption/decryption through the connection.
/// </summary>
public interface ISessionFrameCipher : IDisposable
{
	/// <summary>Gets the wire cipher id announced in the ConfirmEncryptKeyAck frame.</summary>
	byte CipherId { get; }

	/// <summary>Gets the required session key length in bytes.</summary>
	int KeySizeBytes { get; }

	/// <summary>Gets the nonce length in bytes.</summary>
	int NonceSizeBytes { get; }

	/// <summary>Gets the authentication tag length in bytes.</summary>
	int TagSizeBytes { get; }

	/// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="ciphertext"/> plus <paramref name="tag"/>, authenticating <paramref name="associatedData"/> without encrypting it.</summary>
	void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default);

	/// <summary>Decrypts <paramref name="ciphertext"/> plus <paramref name="tag"/> into <paramref name="plaintext"/>. Throws <see cref="CryptographicException"/> when the tag or <paramref name="associatedData"/> does not match.</summary>
	void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default);
}

/// <summary>
/// AES-256-GCM session cipher. AES-GCM was chosen over ChaCha20-Poly1305 because the .NET implementation is
/// hardware accelerated (AES-NI) on virtually every deployment target and is part of the base class library,
/// while the BCL ChaCha20-Poly1305 implementation is platform restricted. The tag size is fixed at 16 bytes.
/// </summary>
public sealed class AesGcmSessionFrameCipher : ISessionFrameCipher, IDisposable
{
	/// <summary>Wire cipher id for AES-256-GCM.</summary>
	public const byte DefaultCipherId = 0x01;

	private const int TagSize = 16;

	private readonly AesGcm _aes;

	/// <summary>Creates the cipher from a 32-byte session key.</summary>
	public AesGcmSessionFrameCipher(ReadOnlySpan<byte> key)
	{
		_aes = new AesGcm(key, TagSize);
	}

	/// <inheritdoc />
	public byte CipherId => DefaultCipherId;

	/// <inheritdoc />
	public int KeySizeBytes => 32;

	/// <inheritdoc />
	public int NonceSizeBytes => AesGcm.NonceByteSizes.MaxSize;

	/// <inheritdoc />
	public int TagSizeBytes => TagSize;

	/// <inheritdoc />
	public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
	{
		_aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
	}

	/// <inheritdoc />
	public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
	{
		_aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
	}

	public void Dispose()
	{
		_aes.Dispose();
	}
}

/// <summary>Creates session frame ciphers from the wire cipher id negotiated during the handshake.</summary>
public static class SessionFrameCipherFactory
{
	/// <summary>
	/// Creates the frame cipher for <paramref name="cipherId"/>. Throws
	/// <see cref="GateHandshakeException"/> with <see cref="GateHandshakeErrors.UnsupportedCipher"/> for
	/// unknown cipher ids.
	/// </summary>
	public static ISessionFrameCipher Create(byte cipherId, ReadOnlySpan<byte> key)
	{
		return cipherId switch
		{
			AesGcmSessionFrameCipher.DefaultCipherId => new AesGcmSessionFrameCipher(key),
			_ => throw new GateHandshakeException(GateHandshakeErrors.UnsupportedCipher, $"Session cipher id 0x{cipherId:X2} is not supported."),
		};
	}
}
