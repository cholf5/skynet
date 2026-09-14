using System.Security.Cryptography;
using FluentAssertions;
using Skynet.Net.Encryption;
using Xunit;
using static Skynet.Net.Tests.GateEncryptionTestHelpers;

namespace Skynet.Net.Tests.Encryption;

public sealed class GateHandshakeCodecTests
{
	[Fact]
	public void DeriveSessionKeyMatchesReferenceHkdf()
	{
		var seed = GateEncryptionTestHelpers.CreateFixedSeed();
		var salt = GateEncryptionTestHelpers.RandomBytes(16);

		var derived = GateHandshakeCodec.DeriveSessionKey(seed, TestCipherId, salt, GateKeyDirection.ClientToGate);

		var expected = HKDF.DeriveKey(
			HashAlgorithmName.SHA256,
			seed,
			GateHandshakeCodec.SessionKeyByteLength,
			salt,
			CombineInfoPrefix(TestCipherId, GateKeyDirection.ClientToGate));
		derived.Should().Equal(expected);
		derived.Should().HaveCount(GateHandshakeCodec.SessionKeyByteLength);
	}

	[Fact]
	public void DeriveSessionKeyUsesDefaultSaltWhenEmpty()
	{
		var seed = GateEncryptionTestHelpers.CreateFixedSeed();

		var withEmptySalt = GateHandshakeCodec.DeriveSessionKey(seed, TestCipherId, ReadOnlySpan<byte>.Empty, GateKeyDirection.ClientToGate);

		withEmptySalt.Should().HaveCount(GateHandshakeCodec.SessionKeyByteLength);
	}

	[Fact]
	public void DeriveSessionKeyBindsCipherIdAndSalt()
	{
		var seed = GateEncryptionTestHelpers.CreateFixedSeed();
		var salt = GateEncryptionTestHelpers.RandomBytes(16);

		var keyA = GateHandshakeCodec.DeriveSessionKey(seed, 0x01, salt, GateKeyDirection.ClientToGate);
		var keyB = GateHandshakeCodec.DeriveSessionKey(seed, 0x02, salt, GateKeyDirection.ClientToGate);
		var keyC = GateHandshakeCodec.DeriveSessionKey(seed, 0x01, GateEncryptionTestHelpers.RandomBytes(16), GateKeyDirection.ClientToGate);

		keyA.Should().NotBeEquivalentTo(keyB);
		keyA.Should().NotBeEquivalentTo(keyC);
	}

	[Fact]
	public void DeriveSessionKeySeparatesDirections()
	{
		// D-5: the same seed must yield two independent keys so no key ever encrypts in both directions.
		var seed = GateEncryptionTestHelpers.CreateFixedSeed();
		var salt = GateEncryptionTestHelpers.RandomBytes(16);

		var clientToGate = GateHandshakeCodec.DeriveSessionKey(seed, TestCipherId, salt, GateKeyDirection.ClientToGate);
		var gateToClient = GateHandshakeCodec.DeriveSessionKey(seed, TestCipherId, salt, GateKeyDirection.GateToClient);

		clientToGate.Should().NotBeEquivalentTo(gateToClient);
		clientToGate.Should().HaveCount(GateHandshakeCodec.SessionKeyByteLength);
		gateToClient.Should().HaveCount(GateHandshakeCodec.SessionKeyByteLength);
	}

	[Fact]
	public void DeriveSessionKeyRejectsWrongSeedLength()
	{
		var act = () => GateHandshakeCodec.DeriveSessionKey(new byte[15], TestCipherId, ReadOnlySpan<byte>.Empty, GateKeyDirection.ClientToGate);
		act.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void RsaPlaintextRoundtripsSeedAndToken()
	{
		var seed = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.SeedByteLength);
		var token = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength);

		var plaintext = GateHandshakeCodec.BuildRsaPlaintext(seed, token, DefaultGateHandshakeRandomSource.Instance);
		var (parsedSeed, parsedToken) = GateHandshakeCodec.ParseRsaPlaintext(plaintext);

		parsedSeed.Should().Equal(seed);
		parsedToken.Should().Equal(token);
		plaintext.Length.Should().BeGreaterThanOrEqualTo(72).And.BeLessThanOrEqualTo(104);
	}

	[Fact]
	public void ParseRsaPlaintextRejectsBadMagic()
	{
		var plaintext = new byte[104];
		RandomNumberGenerator.Fill(plaintext);
		plaintext[0] = 0x00;

		var act = () => GateHandshakeCodec.ParseRsaPlaintext(plaintext);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.InvalidPlaintext);
	}

	[Fact]
	public void ParseRsaPlaintextRejectsTruncatedTail()
	{
		var seed = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.SeedByteLength);
		var token = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength);
		var plaintext = GateHandshakeCodec.BuildRsaPlaintext(seed, token, DefaultGateHandshakeRandomSource.Instance);

		var truncated = plaintext[..^5];
		var act = () => GateHandshakeCodec.ParseRsaPlaintext(truncated);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.InvalidPlaintext);
	}

	[Fact]
	public void ResponseTokenFrameRoundtrips()
	{
		var token = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength);
		var expire = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		var frame = GateFrameCodec.EncodeResponseToken(token, expire);
		var decoded = GateFrameCodec.DecodeResponseToken(frame);

		decoded.Token.Should().Equal(token);
		decoded.ExpireUnixMs.Should().Be(expire);
		frame[0].Should().Be(GateFrameType.HandshakeResponseToken);
	}

	[Fact]
	public void DecodeResponseTokenRejectsMalformedFrame()
	{
		var act = () => GateFrameCodec.DecodeResponseToken([GateFrameType.HandshakeResponseToken, 0x01]);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.UnknownFrame);
	}

	[Fact]
	public void ErrorFrameRoundtrips()
	{
		var frame = GateFrameCodec.EncodeErrorFrame(GateHandshakeErrors.InvalidToken);

		frame[0].Should().Be(GateFrameType.HandshakeError);
		GateFrameCodec.TryDecodeErrorFrame(frame).Should().Be(GateHandshakeErrors.InvalidToken);
		GateFrameCodec.TryDecodeErrorFrame([0x99]).Should().BeNull();
	}

	[Fact]
	public void DefaultRandomSourceProducesRequestedSizes()
	{
		var source = DefaultGateHandshakeRandomSource.Instance;

		for (var i = 0; i < 32; i++)
		{
			var padding = source.GetPadding(GateHandshakeCodec.MinHeadPaddingLength, GateHandshakeCodec.MaxHeadPaddingLength);
			padding.Length.Should().BeInRange(GateHandshakeCodec.MinHeadPaddingLength, GateHandshakeCodec.MaxHeadPaddingLength);
		}

		source.GetTokenBytes(32).Should().HaveCount(32);
		source.GetSeedBytes(16).Should().HaveCount(16);
	}

	private static byte[] CombineInfoPrefix(byte cipherId, GateKeyDirection direction)
	{
		var prefix = "skynet-gate-session-key/v2"u8.ToArray();
		var info = new byte[prefix.Length + 2];
		prefix.CopyTo(info, 0);
		info[^2] = cipherId;
		info[^1] = (byte)direction;
		return info;
	}
}
