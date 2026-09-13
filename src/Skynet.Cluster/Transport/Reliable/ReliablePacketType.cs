namespace Skynet.Cluster.Transport.Reliable;

/// <summary>
/// Identifies the type of a reliable-layer packet exchanged between two <see cref="ReliableQueue"/> peers.
/// </summary>
public enum ReliablePacketType : byte
{
	Data = 0,
	Ack = 1
}
