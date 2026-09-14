namespace Skynet.Net.Encryption;

/// <summary>
/// IPsec-style sliding anti-replay window over 64-bit frame sequence numbers. The receiver tracks the
/// highest authenticated sequence number plus a bitmap of the preceding <see cref="WindowSize"/> slots:
/// sequences above the high-water mark are always acceptable, sequences inside the window are acceptable
/// only when not already marked, and sequences at or below the left edge are rejected outright.
/// </summary>
/// <remarks>
/// Callers must only call <see cref="MarkSeen"/> AFTER the frame passed AEAD authentication: marking
/// before verification would let an attacker burn window slots with forged frames and deny service to
/// the legitimate frames carrying those sequence numbers. Single-thread by contract: one window per
/// direction per connection, updated from the serialized receive loop.
/// </remarks>
public sealed class GateReplayWindow
{
	/// <summary>Default window size in sequence slots. 128 sits mid-range of the IPsec-style 64..256 trade-off and keeps the bitmap at two ulongs.</summary>
	public const int DefaultWindowSize = 128;

	private readonly ulong[] _bitmap;
	private ulong _highest;
	private bool _initialized;

	/// <summary>Creates a replay window. <paramref name="windowSize"/> must be in [64, 1024].</summary>
	public GateReplayWindow(int windowSize = DefaultWindowSize)
	{
		if (windowSize is < 64 or > 1024)
		{
			throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "Replay window size must be in [64, 1024].");
		}

		WindowSize = windowSize;
		_bitmap = new ulong[(windowSize + 63) / 64];
	}

	/// <summary>Gets the window size in sequence slots.</summary>
	public int WindowSize { get; }

	/// <summary>
	/// Returns true when <paramref name="sequence"/> is new: either beyond the high-water mark or an
	/// unmarked slot inside the window. Does not modify the window.
	/// </summary>
	public bool IsAcceptable(ulong sequence)
	{
		if (!_initialized)
		{
			return true;
		}

		if (sequence > _highest)
		{
			// Senders count linearly from 0 and only authenticated frames advance the window, so a forward
			// jump beyond the window size can only come from counter wraparound or a misbehaving peer.
			// Rejecting it keeps the 64-bit space effectively linear (defense in depth).
			return sequence - _highest <= (ulong)WindowSize;
		}

		var distance = _highest - sequence;
		if (distance >= (ulong)WindowSize)
		{
			return false;
		}

		return !IsMarked(distance);
	}

	/// <summary>Marks <paramref name="sequence"/> as seen, advancing the high-water mark when needed. Call only after successful authentication.</summary>
	public void MarkSeen(ulong sequence)
	{
		if (!_initialized)
		{
			_initialized = true;
			_highest = sequence;
			MarkRelative(0);
			return;
		}

		if (sequence > _highest)
		{
			var shift = sequence - _highest;
			_highest = sequence;
			ShiftBitmap(shift);
			MarkRelative(0);
			return;
		}

		var distance = _highest - sequence;
		if (distance < (ulong)WindowSize)
		{
			MarkRelative(distance);
		}
	}

	private bool IsMarked(ulong distance)
	{
		var (word, bit) = Split(distance);
		return (_bitmap[word] & (1UL << bit)) != 0;
	}

	private void MarkRelative(ulong distance)
	{
		var (word, bit) = Split(distance);
		_bitmap[word] |= 1UL << bit;
	}

	private void ShiftBitmap(ulong shift)
	{
		if (shift >= (ulong)_bitmap.Length * 64)
		{
			Array.Clear(_bitmap);
			return;
		}

		// Distance d from the high-water mark maps to bit d; advancing the mark by shift moves every
		// existing bit up by shift. Rewrite from the top so moved bits are not overwritten mid-pass.
		for (var distance = (ulong)(WindowSize - 1); distance >= shift; distance--)
		{
			var source = distance - shift;
			var (word, bit) = Split(distance);
			var (sourceWord, sourceBit) = Split(source);
			var isSet = (_bitmap[sourceWord] & (1UL << sourceBit)) != 0;
			if (isSet)
			{
				_bitmap[word] |= 1UL << bit;
			}
			else
			{
				_bitmap[word] &= ~(1UL << bit);
			}
		}

		for (var distance = shift - 1; distance < (ulong)WindowSize; distance--)
		{
			var (word, bit) = Split(distance);
			_bitmap[word] &= ~(1UL << bit);
		}
	}

	private static (int Word, int Bit) Split(ulong distance)
	{
		return ((int)(distance >> 6), (int)(distance & 63));
	}
}
