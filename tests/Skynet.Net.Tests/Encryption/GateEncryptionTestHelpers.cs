using System.Security.Cryptography;
using Skynet.Net.Encryption;

namespace Skynet.Net.Tests;

/// <summary>
/// Random source that returns a fixed seed while delegating padding and token generation to the real
/// random source, so tests can assert exact key derivation from a known seed.
/// </summary>
internal sealed class FixedSeedRandomSource(byte[] seed) : IGateHandshakeRandomSource
{
	public byte[] GetPadding(int minInclusive, int maxInclusive)
	{
		return DefaultGateHandshakeRandomSource.Instance.GetPadding(minInclusive, maxInclusive);
	}

	public byte[] GetTokenBytes(int length)
	{
		return DefaultGateHandshakeRandomSource.Instance.GetTokenBytes(length);
	}

	public byte[] GetSeedBytes(int length)
	{
		if (length != seed.Length)
		{
			throw new ArgumentException($"FixedSeedRandomSource seed length mismatch: expected {seed.Length}, got {length}.");
		}

		return (byte[])seed.Clone();
	}
}

/// <summary>Shared helpers for the gate encryption test suite.</summary>
internal static class GateEncryptionTestHelpers
{
	public const byte TestCipherId = AesGcmSessionFrameCipher.DefaultCipherId;

	public static byte[] CreateFixedSeed()
	{
		var seed = new byte[GateHandshakeCodec.SeedByteLength];
		for (var i = 0; i < seed.Length; i++)
		{
			seed[i] = (byte)(i + 1);
		}

		return seed;
	}

	public static byte[] RandomBytes(int length)
	{
		var bytes = new byte[length];
		RandomNumberGenerator.Fill(bytes);
		return bytes;
	}
}
