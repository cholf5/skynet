using System.Security.Cryptography;
using FluentAssertions;
using Skynet.Net.Encryption;
using Xunit;
using static Skynet.Net.Tests.GateEncryptionTestHelpers;

namespace Skynet.Net.Tests.Encryption;

public sealed class GateFrameCipherTests
{
	[Fact]
	public void AesGcmCipherRoundtrips()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = new AesGcmSessionFrameCipher(key);
		var plaintext = GateEncryptionTestHelpers.RandomBytes(1234);
		var nonce = GateEncryptionTestHelpers.RandomBytes(cipher.NonceSizeBytes);
		var ciphertext = new byte[plaintext.Length];
		var tag = new byte[cipher.TagSizeBytes];

		cipher.Encrypt(nonce, plaintext, ciphertext, tag);
		var decrypted = new byte[plaintext.Length];
		cipher.Decrypt(nonce, ciphertext, tag, decrypted);

		decrypted.Should().Equal(plaintext);
		cipher.CipherId.Should().Be(TestCipherId);
		cipher.KeySizeBytes.Should().Be(32);
	}

	[Fact]
	public void TamperedCiphertextFailsAuthentication()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = new AesGcmSessionFrameCipher(key);
		var plaintext = GateEncryptionTestHelpers.RandomBytes(64);
		var nonce = GateEncryptionTestHelpers.RandomBytes(cipher.NonceSizeBytes);
		var ciphertext = new byte[plaintext.Length];
		var tag = new byte[cipher.TagSizeBytes];
		cipher.Encrypt(nonce, plaintext, ciphertext, tag);
		ciphertext[0] ^= 0xFF;

		var act = () => cipher.Decrypt(nonce, ciphertext, tag, new byte[plaintext.Length]);

		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void WrongKeyFailsAuthentication()
	{
		var nonce = GateEncryptionTestHelpers.RandomBytes(12);
		var plaintext = GateEncryptionTestHelpers.RandomBytes(64);
		byte[] ciphertext;
		byte[] tag;
		using (var encryptCipher = new AesGcmSessionFrameCipher(GateEncryptionTestHelpers.RandomBytes(32)))
		{
			ciphertext = new byte[plaintext.Length];
			tag = new byte[16];
			encryptCipher.Encrypt(nonce, plaintext, ciphertext, tag);
		}

		using var decryptCipher = new AesGcmSessionFrameCipher(GateEncryptionTestHelpers.RandomBytes(32));
		var act = () => decryptCipher.Decrypt(nonce, ciphertext, tag, new byte[plaintext.Length]);

		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void FrameCodecRoundtrips()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		var plaintext = "business frame payload"u8.ToArray();

		var frame = GateFrameCodec.EncryptFrame(cipher, plaintext, 0);
		var decrypted = GateFrameCodec.DecryptFrame(cipher, frame);

		decrypted.Should().Equal(plaintext);
		frame.Length.Should().Be(GateFrameCodec.SequenceByteLength + cipher.NonceSizeBytes + plaintext.Length + cipher.TagSizeBytes);
	}

	[Fact]
	public void FrameCodecRejectsShortFrames()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);

		var act = () => GateFrameCodec.DecryptFrame(cipher, [0x01, 0x02]);

		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void FactoryRejectsUnknownCipherId()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);

		var act = () => SessionFrameCipherFactory.Create(0xFF, key);

		act.Should().Throw<GateHandshakeException>()
			.Which.Code.Should().Be(GateHandshakeErrors.UnsupportedCipher);
	}
}
