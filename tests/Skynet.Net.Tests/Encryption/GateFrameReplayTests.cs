using System.Security.Cryptography;
using FluentAssertions;
using Skynet.Net.Encryption;
using Xunit;
using static Skynet.Net.Tests.GateEncryptionTestHelpers;

namespace Skynet.Net.Tests.Encryption;

/// <summary>
/// D-5 replay protection: frame sequence numbers as AEAD associated data plus a receiver-side sliding
/// anti-replay window (IPsec style). Also covers the birthday-bound related nonce uniqueness requirement.
/// </summary>
public sealed class GateFrameReplayTests
{
	[Fact]
	public void ReplayWindow_AcceptsFirstFrameAtAnySequence()
	{
		var window = new GateReplayWindow();

		window.IsAcceptable(42).Should().BeTrue();
		window.MarkSeen(42);

		// The window is anchored at the first authenticated frame: 42 becomes the high-water mark.
		window.IsAcceptable(42).Should().BeFalse();
		window.IsAcceptable(43).Should().BeTrue();
		window.IsAcceptable(41).Should().BeTrue("sequence 41 is inside the window below the high-water mark");
	}

	[Fact]
	public void ReplayWindow_RejectsDuplicatesAndOldSequences()
	{
		var window = new GateReplayWindow(windowSize: 128);

		window.MarkSeen(100);
		window.IsAcceptable(100).Should().BeFalse("duplicate of the high-water mark");
		window.MarkSeen(105);
		window.IsAcceptable(105).Should().BeFalse();
		window.IsAcceptable(103).Should().BeTrue("inside the window and never marked");

		// Sequences beyond the window's left edge (highest - 127) are rejected even when never seen.
		var wrapped = unchecked(105UL - 128UL);
		window.IsAcceptable(wrapped).Should().BeFalse();
	}

	[Fact]
	public void ReplayWindow_ShiftsWithHighWaterMark()
	{
		var window = new GateReplayWindow(windowSize: 128);

		window.MarkSeen(1);
		window.MarkSeen(200);

		window.IsAcceptable(1).Should().BeFalse("far outside the window after the mark jumped ahead");
		window.IsAcceptable(73).Should().BeTrue("200 - 73 = 127, the last slot inside the window");
		window.IsAcceptable(72).Should().BeFalse();
		window.IsAcceptable(200).Should().BeFalse();
	}

	[Fact]
	public void ReplayWindow_RejectsInvalidSize()
	{
		var act = () => new GateReplayWindow(32);
		act.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void DecryptFrame_RejectsReplayedFrame()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		var window = new GateReplayWindow();
		var frame = GateFrameCodec.EncryptFrame(cipher, "business"u8.ToArray(), 7);

		GateFrameCodec.DecryptFrame(cipher, frame, window).Should().Equal("business"u8.ToArray());

		var act = () => GateFrameCodec.DecryptFrame(cipher, frame, window);
		act.Should().Throw<CryptographicException>()
			.WithMessage("*replay*");
	}

	[Fact]
	public void DecryptFrame_AcceptsInWindowReorderAndRejectsOutsideWindow()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		var window = new GateReplayWindow(windowSize: 64);

		var frame0 = GateFrameCodec.EncryptFrame(cipher, [0], 0);
		var frame5 = GateFrameCodec.EncryptFrame(cipher, [5], 5);

		GateFrameCodec.DecryptFrame(cipher, frame0, window);
		// Out of order but inside the window: accepted.
		GateFrameCodec.DecryptFrame(cipher, frame5, window).Should().Equal([5]);

		// Below the left edge (highest 5 - window 64): rejected even though never seen.
		var oldSequence = unchecked(5UL - 64UL);
		var oldFrame = GateFrameCodec.EncryptFrame(cipher, [0x99], oldSequence);
		var act = () => GateFrameCodec.DecryptFrame(cipher, oldFrame, window);
		act.Should().Throw<CryptographicException>()
			.WithMessage("*outside the replay window*");
	}

	[Fact]
	public void DecryptFrame_TagCoversSequence()
	{
		// Tampering with the sequence number must fail AEAD authentication even without a replay window,
		// because the sequence is the associated data.
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		var frame = GateFrameCodec.EncryptFrame(cipher, "business"u8.ToArray(), 7);
		frame[7] ^= 0x01; // corrupt the low byte of the big-endian sequence

		var act = () => GateFrameCodec.DecryptFrame(cipher, frame);
		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void DecryptFrame_DoesNotBurnWindowSlotsOnFailedAuthentication()
	{
		// An attacker can read the plaintext sequence; a forged frame must not block the legitimate one.
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		var window = new GateReplayWindow();
		var legit = GateFrameCodec.EncryptFrame(cipher, "legit"u8.ToArray(), 0);

		var forged = (byte[])legit.Clone();
		forged[^1] ^= 0xFF; // break the tag
		var act = () => GateFrameCodec.DecryptFrame(cipher, forged, window);
		act.Should().Throw<CryptographicException>();

		GateFrameCodec.DecryptFrame(cipher, legit, window).Should().Equal("legit"u8.ToArray());
	}

	[Fact]
	public void DecryptFrame_RejectsFramesShorterThanSequencePlusHeader()
	{
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);

		var act = () => GateFrameCodec.DecryptFrame(cipher, new byte[GateFrameCodec.SequenceByteLength + 12 + 15], new GateReplayWindow());
		act.Should().Throw<CryptographicException>();
	}

	[Fact]
	public void BatchEncryption_ProducesUniqueNonces()
	{
		// With random 96-bit nonces the birthday bound puts collision risk at ~2^-32 after 2^32 frames
		// per key. This batch is far below the bound; its only job is to catch systematic reuse bugs
		// (e.g. a nonce derived from a constant or a per-call PRNG reseed).
		var key = GateEncryptionTestHelpers.RandomBytes(32);
		using var cipher = SessionFrameCipherFactory.Create(TestCipherId, key);
		const int frameCount = 10_000;
		var nonces = new HashSet<byte[]>(frameCount);

		for (ulong sequence = 0; sequence < frameCount; sequence++)
		{
			var frame = GateFrameCodec.EncryptFrame(cipher, [(byte)sequence], sequence);
			nonces.Add(frame[GateFrameCodec.SequenceByteLength..(GateFrameCodec.SequenceByteLength + cipher.NonceSizeBytes)].ToArray());
		}

		nonces.Count.Should().Be(frameCount, "every encrypted frame must carry a fresh random nonce");
	}
}
