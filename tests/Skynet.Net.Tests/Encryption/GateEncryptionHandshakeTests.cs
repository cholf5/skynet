using System.Security.Cryptography;
using FluentAssertions;
using Skynet.Net.Encryption;
using Xunit;
using static Skynet.Net.Tests.GateEncryptionTestHelpers;

namespace Skynet.Net.Tests.Encryption;

/// <summary>
/// State machine level handshake tests ported from the reference implementation, adapted to the
/// modernized protocol (OAEP-SHA256, HKDF-SHA256, hand-written RSA plaintext layout).
/// </summary>
public sealed class GateEncryptionHandshakeTests
{
	[Fact]
	public void FullHandshakeDerivesMatchingSessionKeys()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var serverSession = new GateEncryptionServerSession(keyProvider);
		var clientSession = new GateEncryptionClientSession(keyProvider);

		var token = serverSession.IssueToken();
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));
		var serverKey = serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));
		clientSession.ProcessConfirmAck(GateFrameCodec.EncryptFrame(SessionFrameCipherFactory.Create(TestCipherId, serverKey), [TestCipherId]));

		clientSession.IsCompleted.Should().BeTrue();
		clientSession.SessionKey.Should().NotBeNull();
		clientSession.SessionKey.Should().HaveCount(GateHandshakeCodec.SessionKeyByteLength);
		serverSession.SessionKey.Should().Equal(clientSession.SessionKey);
		clientSession.FrameCipher.Should().NotBeNull();
		serverSession.IsCompleted.Should().BeTrue();
	}

	[Fact]
	public void SessionKeyIsHkdfOfClientSeed()
	{
		var fixedSeed = GateEncryptionTestHelpers.CreateFixedSeed();
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var salt = GateEncryptionTestHelpers.RandomBytes(16);
		var serverSession = new GateEncryptionServerSession(keyProvider, hkdfSalt: salt);
		var clientSession = new GateEncryptionClientSession(
			keyProvider,
			randomSource: new FixedSeedRandomSource(fixedSeed),
			hkdfSalt: salt);

		var token = serverSession.IssueToken();
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));
		var serverKey = serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));

		var expected = GateHandshakeCodec.DeriveSessionKey(fixedSeed, TestCipherId, salt);
		serverKey.Should().Equal(expected);
		clientSession.SessionKey.Should().Equal(expected);
	}

	[Fact]
	public void ProcessResponseToken_ShouldExposeTokenExpireUnixMs()
	{
		// The decoded expiry is exposed informationally (the client clock is not authoritative);
		// this locks the behavior that the expiry carried in the response frame is surfaced as-is.
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var clientSession = new GateEncryptionClientSession(keyProvider);

		const long expireUnixMs = 4102444800000; // arbitrary fixed value (2100-01-01 UTC)
		clientSession.TokenExpireUnixMs.Should().BeNull();
		_ = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(
			GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength), expireUnixMs));

		clientSession.TokenExpireUnixMs.Should().Be(expireUnixMs);
	}

	[Fact]
	public void GateRejectsReplayWithMismatchedToken()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var serverSession = new GateEncryptionServerSession(keyProvider);
		_ = serverSession.IssueToken();

		var attackerToken = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength);
		var attackerSeed = GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.SeedByteLength);
		byte[] ciphertext;
		using (var publicKey = keyProvider.CreatePublicKey())
		{
			var plaintext = GateHandshakeCodec.BuildRsaPlaintext(attackerSeed, attackerToken, DefaultGateHandshakeRandomSource.Instance);
			ciphertext = publicKey.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);
		}

		var act = () => serverSession.ProcessConfirmEncryptKey(ciphertext);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.InvalidToken);
	}

	[Fact]
	public void GateRejectsExpiredToken()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var baseTime = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
		var current = baseTime;
		var serverSession = new GateEncryptionServerSession(
			keyProvider,
			tokenLifetime: TimeSpan.FromSeconds(1),
			clock: () => current);
		var clientSession = new GateEncryptionClientSession(keyProvider);

		var token = serverSession.IssueToken();
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));
		current = baseTime.AddSeconds(5);

		var act = () => serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.TokenExpired);
	}

	[Fact]
	public void GateRejectsConfirmBeforeTokenIssued()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var serverSession = new GateEncryptionServerSession(keyProvider);
		var clientSession = new GateEncryptionClientSession(keyProvider);

		var token = new GateEncryptionToken(GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength), long.MaxValue);
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));

		var act = () => serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.UnexpectedConfirm);
	}

	[Fact]
	public void GateRejectsSecondConfirmAfterHandshakeCompleted()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var (serverSession, clientSession, confirm) = CompleteHandshake(keyProvider);

		var act = () => serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.UnexpectedConfirm);
	}

	[Fact]
	public void GateRejectsSecondHandshake()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var (serverSession, _, _) = CompleteHandshake(keyProvider);

		var act = () => serverSession.IssueToken();

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.DuplicateHandshake);
	}

	[Fact]
	public void ClientRejectsDuplicateResponseToken()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var clientSession = new GateEncryptionClientSession(keyProvider);
		var token = new GateEncryptionToken(GateEncryptionTestHelpers.RandomBytes(GateHandshakeCodec.TokenByteLength), long.MaxValue);
		var frame = GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs);
		_ = clientSession.ProcessResponseToken(frame);

		var act = () => clientSession.ProcessResponseToken(frame);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.DuplicateResponse);
	}

	[Fact]
	public async Task ClientRejectsAckWithoutSessionKey()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var clientSession = new GateEncryptionClientSession(keyProvider);

		clientSession.ProcessConfirmAck(GateFrameCodec.EncryptFrame(
			SessionFrameCipherFactory.Create(TestCipherId, GateEncryptionTestHelpers.RandomBytes(32)),
			[TestCipherId]));

		Func<Task> act = () => clientSession.Completion;
		(await act.Should().ThrowAsync<GateHandshakeException>()).Which.Code.Should().Be(GateHandshakeErrors.AckWithoutKey);
	}

	[Fact]
	public async Task ClientFailsWhenAckCannotBeDecrypted()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		// The client uses a different HKDF salt than the gate, so the ack cannot be decrypted.
		var salt = GateEncryptionTestHelpers.RandomBytes(16);
		var serverSession = new GateEncryptionServerSession(keyProvider);
		var clientSession = new GateEncryptionClientSession(keyProvider, hkdfSalt: salt);

		var token = serverSession.IssueToken();
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));
		_ = serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));
		var ack = GateFrameCodec.EncryptFrame(
			SessionFrameCipherFactory.Create(TestCipherId, serverSession.SessionKey!),
			[TestCipherId]);

		clientSession.ProcessConfirmAck(ack);

		clientSession.IsCompleted.Should().BeFalse();
		Func<Task> act = () => clientSession.Completion;
		(await act.Should().ThrowAsync<GateHandshakeException>()).Which.Code.Should().Be(GateHandshakeErrors.AckDecryptFailed);
	}

	[Fact]
	public void GateRejectsGarbageCiphertext()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var serverSession = new GateEncryptionServerSession(keyProvider);
		_ = serverSession.IssueToken();

		var act = () => serverSession.ProcessConfirmEncryptKey(GateEncryptionTestHelpers.RandomBytes(256));

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.RsaDecryptFailed);
	}

	[Fact]
	public void GateRejectsValidRsaWithMalformedPlaintext()
	{
		using var keyProvider = InMemoryGateRsaKeyProvider.Generate();
		var serverSession = new GateEncryptionServerSession(keyProvider);
		_ = serverSession.IssueToken();

		byte[] ciphertext;
		using (var publicKey = keyProvider.CreatePublicKey())
		{
			ciphertext = publicKey.Encrypt(GateEncryptionTestHelpers.RandomBytes(80), RSAEncryptionPadding.OaepSHA256);
		}

		var act = () => serverSession.ProcessConfirmEncryptKey(ciphertext);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.InvalidPlaintext);
	}

	private static (GateEncryptionServerSession Server, GateEncryptionClientSession Client, byte[] Confirm) CompleteHandshake(
		IGateRsaKeyProvider keyProvider)
	{
		var serverSession = new GateEncryptionServerSession(keyProvider);
		var clientSession = new GateEncryptionClientSession(keyProvider);
		var token = serverSession.IssueToken();
		var confirm = clientSession.ProcessResponseToken(GateFrameCodec.EncodeResponseToken(token.Token, token.ExpireUnixMs));
		_ = serverSession.ProcessConfirmEncryptKey(confirm.AsSpan(1));
		return (serverSession, clientSession, confirm);
	}
}
