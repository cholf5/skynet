using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Skynet.Net.Encryption;

/// <summary>
/// Wire frame types used before the session encryption is active. Encrypted post-handshake frames are not
/// tagged with a frame type: after the handshake completes every inbound frame is decrypted as a whole, so
/// random ciphertext bytes can never be mistaken for a handshake frame.
/// </summary>
public static class GateFrameType
{
	/// <summary>Client to gate: request a token. Empty body.</summary>
	public const byte HandshakeRequestToken = 0x01;

	/// <summary>Gate to client: token response. Body: token (32 bytes) + expire unix ms (8 bytes, big-endian).</summary>
	public const byte HandshakeResponseToken = 0x02;

	/// <summary>Client to gate: confirm session key. Body: RSA-OAEP-SHA256 ciphertext.</summary>
	public const byte HandshakeConfirmKey = 0x03;

	/// <summary>Gate to client: confirm ack. Encrypted frame; plaintext is the negotiated cipher id (1 byte).</summary>
	public const byte HandshakeConfirmAck = 0x04;

	/// <summary>Gate to client: handshake failure. Body: UTF-8 error code.</summary>
	public const byte HandshakeError = 0x05;
}

/// <summary>
/// Encodes and decodes the gate handshake wire frames and the encrypted session frame layout.
/// Encrypted frame layout: sequence (8 bytes, big-endian) || nonce (12 bytes) || ciphertext || tag (16 bytes).
/// The sequence number doubles as the AEAD associated data, so the tag covers it: any tampering with the
/// sequence fails authentication, and the receiver-side sliding window (see <see cref="GateReplayWindow"/>)
/// rejects replays.
/// </summary>
public static class GateFrameCodec
{
	/// <summary>Length of the per-frame sequence number that prefixes every encrypted frame (big-endian ulong).</summary>
	public const int SequenceByteLength = 8;

	/// <summary>Encodes an empty RequestEncryptToken frame.</summary>
	public static byte[] EncodeRequestToken()
	{
		return [GateFrameType.HandshakeRequestToken];
	}

	/// <summary>Encodes a ResponseEncryptToken frame body.</summary>
	public static byte[] EncodeResponseToken(ReadOnlySpan<byte> token, long expireUnixMs)
	{
		if (token.Length != GateHandshakeCodec.TokenByteLength)
		{
			throw new ArgumentException($"Token must be exactly {GateHandshakeCodec.TokenByteLength} bytes.", nameof(token));
		}

		var frame = new byte[1 + token.Length + 8];
		frame[0] = GateFrameType.HandshakeResponseToken;
		token.CopyTo(frame.AsSpan(1));
		BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1 + token.Length), expireUnixMs);
		return frame;
	}

	/// <summary>
	/// Decodes a ResponseEncryptToken frame body. Throws <see cref="GateHandshakeException"/> with
	/// <see cref="GateHandshakeErrors.UnknownFrame"/> when the layout is invalid.
	/// </summary>
	public static GateEncryptionToken DecodeResponseToken(ReadOnlySpan<byte> frame)
	{
		if (frame.Length != 1 + GateHandshakeCodec.TokenByteLength + 8 || frame[0] != GateFrameType.HandshakeResponseToken)
		{
			throw new GateHandshakeException(GateHandshakeErrors.UnknownFrame, "Gate ResponseEncryptToken frame has an invalid layout.");
		}

		var token = frame.Slice(1, GateHandshakeCodec.TokenByteLength).ToArray();
		var expire = BinaryPrimitives.ReadInt64BigEndian(frame[(1 + GateHandshakeCodec.TokenByteLength)..]);
		return new GateEncryptionToken(token, expire);
	}

	/// <summary>Encodes a ConfirmEncryptKey frame wrapping the RSA ciphertext.</summary>
	public static byte[] EncodeConfirmKey(ReadOnlySpan<byte> rsaCiphertext)
	{
		var frame = new byte[1 + rsaCiphertext.Length];
		frame[0] = GateFrameType.HandshakeConfirmKey;
		rsaCiphertext.CopyTo(frame.AsSpan(1));
		return frame;
	}

	/// <summary>Encodes a HandshakeError frame carrying the UTF-8 error code.</summary>
	public static byte[] EncodeErrorFrame(string errorCode)
	{
		ArgumentNullException.ThrowIfNull(errorCode);
		var body = Encoding.UTF8.GetBytes(errorCode);
		var frame = new byte[1 + body.Length];
		frame[0] = GateFrameType.HandshakeError;
		body.CopyTo(frame.AsSpan(1));
		return frame;
	}

	/// <summary>Decodes a HandshakeError frame; returns null when the frame is not an error frame.</summary>
	public static string? TryDecodeErrorFrame(ReadOnlySpan<byte> frame)
	{
		if (frame.Length < 1 || frame[0] != GateFrameType.HandshakeError)
		{
			return null;
		}

		return Encoding.UTF8.GetString(frame[1..]);
	}

	/// <summary>
	/// Encrypts a plaintext into a session frame: sequence || nonce || ciphertext || tag. The sequence
	/// doubles as the AEAD associated data; senders must use a monotonically increasing counter per
	/// direction (the sequence space restarts at zero for every handshake).
	/// </summary>
	public static byte[] EncryptFrame(ISessionFrameCipher cipher, ReadOnlySpan<byte> plaintext, ulong sequence)
	{
		ArgumentNullException.ThrowIfNull(cipher);
		var nonce = RandomNumberGenerator.GetBytes(cipher.NonceSizeBytes);
		var ciphertext = new byte[plaintext.Length];
		var tag = new byte[cipher.TagSizeBytes];
		Span<byte> sequenceBytes = stackalloc byte[SequenceByteLength];
		BinaryPrimitives.WriteUInt64BigEndian(sequenceBytes, sequence);
		cipher.Encrypt(nonce, plaintext, ciphertext, tag, sequenceBytes);

		var frame = new byte[SequenceByteLength + nonce.Length + ciphertext.Length + tag.Length];
		sequenceBytes.CopyTo(frame);
		nonce.CopyTo(frame, SequenceByteLength);
		ciphertext.CopyTo(frame, SequenceByteLength + nonce.Length);
		tag.CopyTo(frame, SequenceByteLength + nonce.Length + ciphertext.Length);
		return frame;
	}

	/// <summary>
	/// Decrypts a session frame. When <paramref name="replayWindow"/> is provided, the frame sequence is
	/// checked against the window first and marked as seen only after authentication succeeded, so failed
	/// authentication never burns window slots. Throws <see cref="CryptographicException"/> when the frame
	/// is malformed, a replay / outside the replay window, or fails authentication.
	/// </summary>
	public static byte[] DecryptFrame(ISessionFrameCipher cipher, ReadOnlySpan<byte> frame, GateReplayWindow? replayWindow = null)
	{
		ArgumentNullException.ThrowIfNull(cipher);
		var headerLength = SequenceByteLength + cipher.NonceSizeBytes + cipher.TagSizeBytes;
		if (frame.Length < headerLength)
		{
			throw new CryptographicException("Session frame is shorter than sequence plus nonce plus tag.");
		}

		var sequence = BinaryPrimitives.ReadUInt64BigEndian(frame[..SequenceByteLength]);
		if (replayWindow is not null && !replayWindow.IsAcceptable(sequence))
		{
			throw new CryptographicException($"Session frame sequence {sequence} was replayed or is outside the replay window.");
		}

		var nonce = frame[SequenceByteLength..(SequenceByteLength + cipher.NonceSizeBytes)];
		var tag = frame[^cipher.TagSizeBytes..];
		var ciphertext = frame[(SequenceByteLength + cipher.NonceSizeBytes)..^cipher.TagSizeBytes];
		var plaintext = new byte[ciphertext.Length];
		cipher.Decrypt(nonce, ciphertext, tag, plaintext, frame[..SequenceByteLength]);
		replayWindow?.MarkSeen(sequence);
		return plaintext;
	}
}
