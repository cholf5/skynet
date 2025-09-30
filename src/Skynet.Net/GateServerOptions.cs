using System.Net;

namespace Skynet.Net;

/// <summary>
/// Configuration for <see cref="GateServer"/>.
/// </summary>
public sealed class GateServerOptions
{
	private string _webSocketPath = "/ws/";

	/// <summary>
	/// Gets or sets a value indicating whether TCP connections are accepted.
	/// </summary>
	public bool EnableTcp { get; set; } = true;

	/// <summary>
	/// Gets or sets the IP address used for the TCP listener.
	/// </summary>
	public IPAddress TcpAddress { get; set; } = IPAddress.Loopback;

	/// <summary>
	/// Gets or sets the TCP port. Set to 0 to let the OS select a dynamic port.
	/// </summary>
	public int TcpPort { get; set; } = 2112;

	/// <summary>
	/// Gets or sets the backlog used by the TCP listener.
	/// </summary>
	public int TcpBacklog { get; set; } = 100;

	/// <summary>
	/// Gets or sets a value indicating whether WebSocket connections are accepted.
	/// </summary>
	public bool EnableWebSockets { get; set; } = true;

	/// <summary>
	/// Gets or sets the HTTP host binding for the WebSocket listener.
	/// </summary>
	public string WebSocketHost { get; set; } = "localhost";

	/// <summary>
	/// Gets or sets the public host used to construct the advertised WebSocket URI. Defaults to <see cref="WebSocketHost"/>.
	/// </summary>
	public string? PublicWebSocketHost { get; set; }

	/// <summary>
	/// Gets or sets the WebSocket port.
	/// </summary>
	public int WebSocketPort { get; set; } = 8080;

	/// <summary>
	/// Gets or sets the request path for WebSocket upgrades.
	/// </summary>
	public string WebSocketPath
	{
		get => _webSocketPath;
		set => _webSocketPath = string.IsNullOrWhiteSpace(value) ? "/ws/" : value;
	}

	/// <summary>
	/// Gets or sets the maximum allowed payload size for a single TCP message in bytes.
	/// </summary>
	public int MaxMessageBytes { get; set; } = 1024 * 1024;

	/// <summary>
	/// Gets or sets the buffer size for WebSocket receives.
	/// </summary>
	public int ReceiveBufferBytes { get; set; } = 16 * 1024;

	/// <summary>
	/// Gets or sets the idle timeout after which a session is considered disconnected.
	/// </summary>
	public TimeSpan? ClientIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>
	/// Gets or sets the router factory responsible for handling session messages.
	/// </summary>
	public Func<SessionContext, ISessionMessageRouter>? RouterFactory { get; set; }

	/// <summary>
	/// Validates the configuration and throws if invalid.
	/// </summary>
	public void Validate()
	{
		if (RouterFactory is null)
		{
			throw new InvalidOperationException("GateServerOptions.RouterFactory must be provided.");
		}

		if (EnableTcp)
		{
			if (TcpPort < 0 || TcpPort > IPEndPoint.MaxPort)
			{
				throw new InvalidOperationException("TCP port must be between 0 and 65535.");
			}
		}

		if (EnableWebSockets)
		{
			if (WebSocketPort <= 0 || WebSocketPort > IPEndPoint.MaxPort)
			{
				throw new InvalidOperationException("WebSocket port must be between 1 and 65535.");
			}
			if (string.IsNullOrWhiteSpace(WebSocketHost))
			{
				throw new InvalidOperationException("WebSocketHost must be specified when WebSockets are enabled.");
			}
		}

		if (MaxMessageBytes <= 0)
		{
			throw new InvalidOperationException("MaxMessageBytes must be positive.");
		}

		if (ReceiveBufferBytes < 1024)
		{
			throw new InvalidOperationException("ReceiveBufferBytes must be at least 1024 bytes.");
		}
	}
}
