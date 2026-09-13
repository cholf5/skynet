using System.Security.Cryptography;

namespace Skynet.Net.Encryption;

/// <summary>
/// Pure helpers for the gate encryption handshake. Ported from the reference implementation with three
/// modernizations: the RSA plaintext is a hand-written byte layout instead of a protobuf envelope, the
/// session key is derived with HKDF-SHA256 instead of SHA1(seed), and the default RSA padding is
/// OAEP-SHA256 instead of PKCS#1 v1.5.
/// </summary>
public static class GateHandshakeCodec
{
	/// <summary>Length of the random seed the client generates per handshake.</summary>
	public const int SeedByteLength = 16;

	/// <summary>Length of the anti-replay token minted by the gate for each RequestEncryptToken.</summary>
	public const int TokenByteLength = 32;

	/// <summary>Length of the session key produced by <see cref="DeriveSessionKey"/> (256-bit, matching AES-256-GCM).</summary>
	public const int SessionKeyByteLength = 32;

	/// <summary>Inclusive minimum length of the head padding block in bytes.</summary>
	public const int MinHeadPaddingLength = 5;

	/// <summary>Inclusive maximum length of the head padding block in bytes.</summary>
	public const int MaxHeadPaddingLength = 20;

	/// <summary>Inclusive minimum length of the tail padding block in bytes.</summary>
	public const int MinTailPaddingLength = 13;

	/// <summary>Inclusive maximum length of the tail padding block in bytes.</summary>
	public const int MaxTailPaddingLength = 30;

	/// <summary>Magic prefix of the RSA plaintext: ASCII "SGK1".</summary>
	public static ReadOnlySpan<byte> PlaintextMagic => [0x53, 0x47, 0x4B, 0x31];

	/// <summary>Default HKDF salt used when no explicit salt is configured.</summary>
	private static ReadOnlySpan<byte> DefaultSalt => "skynet-gate-hkdf-default-salt/v1"u8;

	private static ReadOnlySpan<byte> InfoPrefix => "skynet-gate-session-key/v1"u8;

	/// <summary>
	/// Derives the session key from the client seed using HKDF-SHA256. The HKDF salt can be overridden by
	/// deployment configuration; the HKDF info binds the key to the gate protocol version and to the
	/// negotiated session cipher id, so keys are never reused across cipher choices.
	/// </summary>
	public static byte[] DeriveSessionKey(ReadOnlySpan<byte> seed, byte cipherId, ReadOnlySpan<byte> salt)
	{
		if (seed.Length != SeedByteLength)
		{
			throw new ArgumentException($"Gate handshake seed must be exactly {SeedByteLength} bytes.", nameof(seed));
		}

		var effectiveSalt = salt.IsEmpty ? DefaultSalt : salt;
		var info = new byte[InfoPrefix.Length + 1];
		InfoPrefix.CopyTo(info);
		info[^1] = cipherId;
		var output = new byte[SessionKeyByteLength];
		HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, output, effectiveSalt, info);
		return output;
	}

	/// <summary>
	/// Builds the RSA plaintext for a ConfirmEncryptKey frame. Wire layout (big-endian lengths):
	/// <list type="bullet">
	/// <item>bytes 0-3: magic "SGK1" (0x53 0x47 0x4B 0x31)</item>
	/// <item>bytes 4-19: client seed (16 bytes)</item>
	/// <item>bytes 20-51: echoed server token (32 bytes, anti-replay tie-back)</item>
	/// <item>byte 52: head padding length (5..20), followed by that many random bytes</item>
	/// <item>then: tail padding length (13..30), followed by that many random bytes</item>
	/// </list>
	/// Total plaintext length is 54 + head + tail (72..104 bytes). The padding blocks do not add
	/// cryptographic strength (OAEP is already randomized) but keep the plaintext layout close to the
	/// reference protocol and avoid fixed-size plaintexts.
	/// </summary>
	public static byte[] BuildRsaPlaintext(
		ReadOnlySpan<byte> seed,
		ReadOnlySpan<byte> echoedToken,
		IGateHandshakeRandomSource randomSource)
	{
		ArgumentNullException.ThrowIfNull(randomSource);
		ValidateSeed(seed);
		ValidateToken(echoedToken);

		var headPadding = randomSource.GetPadding(MinHeadPaddingLength, MaxHeadPaddingLength);
		var tailPadding = randomSource.GetPadding(MinTailPaddingLength, MaxTailPaddingLength);

		var plain = new byte[PlaintextMagic.Length + SeedByteLength + TokenByteLength + 1 + headPadding.Length + 1 + tailPadding.Length];
		var offset = 0;
		PlaintextMagic.CopyTo(plain);
		offset += PlaintextMagic.Length;
		seed.CopyTo(plain.AsSpan(offset));
		offset += SeedByteLength;
		echoedToken.CopyTo(plain.AsSpan(offset));
		offset += TokenByteLength;
		plain[offset++] = (byte)headPadding.Length;
		headPadding.CopyTo(plain.AsSpan(offset));
		offset += headPadding.Length;
		plain[offset++] = (byte)tailPadding.Length;
		tailPadding.CopyTo(plain.AsSpan(offset));
		return plain;
	}

	/// <summary>
	/// Parses the RSA plaintext produced by <see cref="BuildRsaPlaintext"/>. Throws
	/// <see cref="GateHandshakeException"/> with <see cref="GateHandshakeErrors.InvalidPlaintext"/> when the
	/// layout does not match. Callers must validate the echoed token against the issued token.
	/// </summary>
	public static (byte[] Seed, byte[] EchoedToken) ParseRsaPlaintext(ReadOnlySpan<byte> plaintext)
	{
		if (plaintext.Length < PlaintextMagic.Length + SeedByteLength + TokenByteLength + 2
			|| !plaintext[..PlaintextMagic.Length].SequenceEqual(PlaintextMagic))
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidPlaintext, "Gate handshake RSA plaintext has an invalid magic or is too short.");
		}

		var offset = PlaintextMagic.Length;
		var seed = plaintext[offset..(offset + SeedByteLength)].ToArray();
		offset += SeedByteLength;
		var echoedToken = plaintext[offset..(offset + TokenByteLength)].ToArray();
		offset += TokenByteLength;

		if (offset >= plaintext.Length)
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidPlaintext, "Gate handshake RSA plaintext is missing the head padding length.");
		}

		var headLength = plaintext[offset++];
		if (headLength < MinHeadPaddingLength || headLength > MaxHeadPaddingLength || offset + headLength >= plaintext.Length)
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidPlaintext, "Gate handshake RSA plaintext has an invalid head padding block.");
		}

		offset += headLength;
		var tailLength = plaintext[offset++];
		if (tailLength < MinTailPaddingLength || tailLength > MaxTailPaddingLength || offset + tailLength != plaintext.Length)
		{
			throw new GateHandshakeException(GateHandshakeErrors.InvalidPlaintext, "Gate handshake RSA plaintext has an invalid tail padding block.");
		}

		return (seed, echoedToken);
	}

	/// <summary>Mints a fresh token: <paramref name="randomSource"/> provides 32 random bytes.</summary>
	public static byte[] MintTokenBytes(IGateHandshakeRandomSource randomSource)
	{
		ArgumentNullException.ThrowIfNull(randomSource);
		return randomSource.GetTokenBytes(TokenByteLength);
	}

	private static void ValidateSeed(ReadOnlySpan<byte> seed)
	{
		if (seed.Length != SeedByteLength)
		{
			throw new ArgumentException($"Gate handshake seed must be exactly {SeedByteLength} bytes.", nameof(seed));
		}
	}

	private static void ValidateToken(ReadOnlySpan<byte> token)
	{
		if (token.Length != TokenByteLength)
		{
			throw new ArgumentException($"Gate handshake token must be exactly {TokenByteLength} bytes.", nameof(token));
		}
	}
}

/// <summary>
/// Source of random bytes for padding, tokens and seeds. The production implementation backs every call
/// with <see cref="RandomNumberGenerator"/>; tests can supply deterministic sequences.
/// </summary>
public interface IGateHandshakeRandomSource
{
	/// <summary>Returns random padding bytes with a length in [<paramref name="minInclusive"/>, <paramref name="maxInclusive"/>].</summary>
	byte[] GetPadding(int minInclusive, int maxInclusive);

	/// <summary>Returns <paramref name="length"/> random token bytes.</summary>
	byte[] GetTokenBytes(int length);

	/// <summary>Returns <paramref name="length"/> random seed bytes.</summary>
	byte[] GetSeedBytes(int length);
}

/// <summary>Default random source backed by <see cref="RandomNumberGenerator"/>.</summary>
public sealed class DefaultGateHandshakeRandomSource : IGateHandshakeRandomSource
{
	public static DefaultGateHandshakeRandomSource Instance { get; } = new();

	public byte[] GetPadding(int minInclusive, int maxInclusive)
	{
		if (minInclusive < 0 || maxInclusive < minInclusive)
		{
			throw new ArgumentOutOfRangeException(nameof(maxInclusive), $"Invalid padding range [{minInclusive}, {maxInclusive}].");
		}

		return RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(minInclusive, maxInclusive + 1));
	}

	public byte[] GetTokenBytes(int length)
	{
		if (length <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(length), length, "Token length must be positive.");
		}

		return RandomNumberGenerator.GetBytes(length);
	}

	public byte[] GetSeedBytes(int length)
	{
		if (length <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(length), length, "Seed length must be positive.");
		}

		return RandomNumberGenerator.GetBytes(length);
	}
}
