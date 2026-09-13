namespace Skynet.Net;

/// <summary>
/// Opens outbound connections to a Gate endpoint. Payloads are opaque byte frames; the pool
/// knows nothing about the protocol carried on top. Implement this to plug in any transport
/// (raw TCP, WebSocket, or fakes in tests).
/// </summary>
public interface IGateClientTransport
{
	Task<IGateProxyConnection> ConnectAsync(GateEndpoint endpoint, CancellationToken cancellationToken = default);
}
