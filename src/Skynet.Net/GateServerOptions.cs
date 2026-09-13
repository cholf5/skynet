using System.Net;
using Skynet.Net.Encryption;

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
	/// Gets or sets a value indicating whether connections must complete the RSA token encryption handshake
	/// before business frames are accepted. Defaults to <c>false</c> to keep wire compatibility with
	/// unencrypted clients; production deployments exposed to untrusted networks should enable it.
	/// </summary>
	public bool EnableEncryption { get; set; }

	/// <summary>
	/// Gets or sets the RSA key provider used by the encryption handshake. When null and
	/// <see cref="EnableEncryption"/> is set, the gate generates a fresh 2048-bit keypair on every
	/// <see cref="GateServer.StartAsync"/>. Provide a persistent implementation for production.
	/// </summary>
	public IGateRsaKeyProvider? RsaKeyProvider { get; set; }

	/// <summary>
	/// Gets or sets the maximum time a connection may spend in the handshake phase before the gate closes it.
	/// </summary>
	public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>Gets or sets the lifetime of handshake tokens issued by the gate.</summary>
	public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>Gets or sets the session cipher id announced to clients. Only AES-256-GCM (0x01) is built in.</summary>
	public byte SessionCipherId { get; set; } = AesGcmSessionFrameCipher.DefaultCipherId;

	/// <summary>
	/// Gets or sets an explicit HKDF salt for session key derivation. When empty the built-in default salt
	/// is used. Both endpoints must use the same salt.
	/// </summary>
	public byte[]? HkdfSalt { get; set; }

	/// <summary>
	/// Gets or sets the authentication hook. In encrypted mode it runs after the handshake key confirmation
	/// succeeded but before the session actor is created; in plaintext mode it runs as soon as the
	/// connection is accepted. Returning false closes the connection with the
	/// <see cref="GateHandshakeErrors.AuthRejected"/> error code.
	/// </summary>
	public Func<GateAuthenticationContext, CancellationToken, ValueTask<bool>>? AuthCallback { get; set; }

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

		if (EnableEncryption)
		{
			if (HandshakeTimeout <= TimeSpan.Zero)
			{
				throw new InvalidOperationException("HandshakeTimeout must be positive when encryption is enabled.");
			}

			if (TokenLifetime <= TimeSpan.Zero)
			{
				throw new InvalidOperationException("TokenLifetime must be positive when encryption is enabled.");
			}

			if (SessionCipherId != AesGcmSessionFrameCipher.DefaultCipherId)
			{
				throw new InvalidOperationException($"SessionCipherId 0x{SessionCipherId:X2} is not supported.");
			}
		}
	}
}
