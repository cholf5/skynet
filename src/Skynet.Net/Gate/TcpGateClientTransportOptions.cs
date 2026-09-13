namespace Skynet.Net;

/// <summary>
/// Configuration for <see cref="TcpGateClientTransport"/>.
/// </summary>
public sealed class TcpGateClientTransportOptions
{
	/// <summary>
	/// Gets or sets the maximum allowed payload size for a single inbound frame in bytes. Frames
	/// whose length prefix exceeds this limit are rejected and the connection is closed. Defaults
	/// to 1 MB, mirroring <see cref="GateServerOptions.MaxMessageBytes"/>.
	/// </summary>
	public int MaxFrameBytes { get; set; } = 1024 * 1024;
}
