namespace Skynet.Cluster.Transport.Reliable;

/// <summary>
/// Represents one unacknowledged outgoing message tracked by a <see cref="ReliableQueue"/>.
/// </summary>
public sealed class ReliableSegment
{
	public ushort Sn { get; init; }

	public ushort Una { get; set; }

	public byte[] Context { get; init; } = [];

	public byte[] Message { get; init; } = [];

	/// <summary>Gets or sets the absolute time at which the next timeout retransmission is due.</summary>
	public DateTimeOffset ResendAtUtc { get; set; }

	/// <summary>Gets or sets the retransmission timeout backing this segment (doubled on every timeout retransmit).</summary>
	public TimeSpan Rto { get; set; }

	/// <summary>Gets or sets the time of the most recent transmission; used for RTT sampling.</summary>
	public DateTimeOffset LastSendAtUtc { get; set; }

	public uint FastAck { get; set; }

	public uint Xmit { get; set; }

	public ReliablePacket ToPacket()
	{
		return ReliablePacket.Data(Sn, Una, Context, Message);
	}
}
