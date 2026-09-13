using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;
using Skynet.Net.Encryption;

namespace Skynet.Net;

/// <summary>
/// Hosts TCP and WebSocket endpoints that bridge external clients into the actor runtime.
/// </summary>
public sealed class GateServer : IAsyncDisposable
{
	private readonly ActorSystem _system;
	private readonly GateServerOptions _options;
	private readonly ILogger<GateServer> _logger;
	private readonly ConcurrentDictionary<string, SessionRuntime> _sessions = new(StringComparer.Ordinal);
	private CancellationTokenSource? _lifetimeCts;
	private TcpListener? _tcpListener;
	private HttpListener? _webSocketListener;
	private Task? _tcpAcceptLoop;
	private Task? _webSocketAcceptLoop;
	private InMemoryGateRsaKeyProvider? _generatedKeyProvider;
	private bool _disposed;

	public GateServer(ActorSystem system, GateServerOptions options, ILogger<GateServer>? logger = null)
	{
		_system = system ?? throw new ArgumentNullException(nameof(system));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_logger = logger ?? NullLogger<GateServer>.Instance;
		_options.Validate();
	}

	/// <summary>
	/// Gets the TCP endpoint the server is listening on.
	/// </summary>
	public IPEndPoint? TcpEndpoint
	{
		get;
		private set;
	}

	/// <summary>
	/// Gets the public WebSocket URI clients should connect to.
	/// </summary>
	public Uri? WebSocketEndpoint
	{
		get;
		private set;
	}

	/// <summary>
	/// Starts the gate server.
	/// </summary>
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_lifetimeCts is not null)
		{
			throw new InvalidOperationException("Gate server already started.");
		}

		_lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var token = _lifetimeCts.Token;

		if (_options.EnableEncryption && _options.RsaKeyProvider is null)
		{
			_generatedKeyProvider?.Dispose();
			_generatedKeyProvider = InMemoryGateRsaKeyProvider.Generate(2048);
			_logger.LogInformation("Gate encryption enabled with a freshly generated 2048-bit RSA keypair.");
		}

		if (_options.EnableTcp)
		{
			_tcpListener = new TcpListener(_options.TcpAddress, _options.TcpPort);
			_tcpListener.Start(_options.TcpBacklog);
			TcpEndpoint = (IPEndPoint)_tcpListener.LocalEndpoint;
			_tcpAcceptLoop = Task.Run(() => AcceptTcpAsync(token), CancellationToken.None);
			_logger.LogInformation("Gate server listening for TCP connections on {Endpoint}.", TcpEndpoint);
		}

		if (_options.EnableWebSockets)
		{
			_webSocketListener = new HttpListener();
			var path = NormalizePath(_options.WebSocketPath);
			_webSocketListener.Prefixes.Add($"http://{_options.WebSocketHost}:{_options.WebSocketPort}{path}");
			_webSocketListener.Start();
			var publicHost = _options.PublicWebSocketHost ?? _options.WebSocketHost;
			WebSocketEndpoint = new Uri($"ws://{publicHost}:{_options.WebSocketPort}{path}");
			_webSocketAcceptLoop = Task.Run(() => AcceptWebSocketsAsync(token), CancellationToken.None);
			_logger.LogInformation("Gate server listening for WebSocket connections on {Endpoint}.", WebSocketEndpoint);
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Stops the gate server and closes all active sessions.
	/// </summary>
	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		var cts = _lifetimeCts;
		if (cts is null)
		{
			return;
		}

		await cts.CancelAsync();
		_tcpListener?.Stop();
		_tcpListener = null;
		TcpEndpoint = null;

		if (_webSocketListener is not null)
		{
			try
			{
				_webSocketListener.Stop();
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Stopping WebSocket listener threw an exception.");
			}
		}

		var acceptTasks = new List<Task>(2);
		if (_tcpAcceptLoop is not null)
		{
			acceptTasks.Add(_tcpAcceptLoop);
		}

		if (_webSocketAcceptLoop is not null)
		{
			acceptTasks.Add(_webSocketAcceptLoop);
		}

		foreach (var task in acceptTasks)
		{
			try
			{
				await task.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Gate server accept loop ended with an error.");
			}
		}

		_tcpAcceptLoop = null;
		_webSocketAcceptLoop = null;

		foreach (var session in _sessions.Values)
		{
			try
			{
				await session.Actor
					.SendAsync(new SessionCloseMessage(SessionCloseReason.ServerShutdown, "Gate server stopping"),
						cts.Token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to notify session {SessionId} about shutdown.",
					session.Metadata.SessionId);
			}
		}

		_sessions.Clear();
		_webSocketListener?.Close();
		_webSocketListener = null;
		WebSocketEndpoint = null;

		_generatedKeyProvider?.Dispose();
		_generatedKeyProvider = null;

		cts.Dispose();
		_lifetimeCts = null;
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await StopAsync().ConfigureAwait(false);
	}

	private async Task AcceptTcpAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient? client;
			try
			{
				client = await _tcpListener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "TCP accept loop failed.");
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
				}

				continue;
			}

			_ = Task.Run(() => HandleTcpClientAsync(client, cancellationToken), CancellationToken.None);
		}
	}

	private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		var sessionId = Guid.NewGuid().ToString("N");
		var connection = new TcpSessionConnection(client);
		var metadata = new SessionMetadata(sessionId, "tcp", client.Client.RemoteEndPoint, DateTimeOffset.UtcNow);
		var stream = client.GetStream();
		var header = new byte[4];
		var maxBytes = _options.MaxMessageBytes;

		async ValueTask<InboundFrame> ReadFrameAsync(CancellationToken token)
		{
			if (!await ReadExactAsync(stream, header, token).ConfigureAwait(false))
			{
				return new InboundFrame(null, SessionCloseReason.ClientDisconnected, null);
			}

			var length = BinaryPrimitives.ReadInt32BigEndian(header);
			if (length < 0 || length > maxBytes)
			{
				return new InboundFrame(null, SessionCloseReason.ProtocolViolation, $"Invalid message length {length}.");
			}

			var payload = new byte[length];
			if (length > 0 && !await ReadExactAsync(stream, payload, token).ConfigureAwait(false))
			{
				return new InboundFrame(null, SessionCloseReason.ClientDisconnected, null);
			}

			return new InboundFrame(payload, SessionCloseReason.ClientDisconnected, null);
		}

		await RunSessionAsync(connection, metadata, ReadFrameAsync, cancellationToken).ConfigureAwait(false);
	}

	private async Task MonitorIdleAsync(SessionRuntime runtime, CancellationToken cancellationToken)
	{
		var idle = _options.ClientIdleTimeout;
		if (idle is null || idle <= TimeSpan.Zero)
		{
			return;
		}

		var timeout = idle.Value;
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
				var last = runtime.Connection.LastActivity;
				if (DateTimeOffset.UtcNow - last >= timeout)
				{
					await runtime.Actor.SendAsync(new SessionHeartbeatTimeoutMessage(timeout), CancellationToken.None)
						.ConfigureAwait(false);
					break;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task AcceptWebSocketsAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			HttpListenerContext? context;
			try
			{
				context = await _webSocketListener!.GetContextAsync().ConfigureAwait(false);
			}
			catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "WebSocket accept loop failed.");
				continue;
			}

			if (!context.Request.IsWebSocketRequest)
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				context.Response.Close();
				continue;
			}

			WebSocketContext? wsContext;
			try
			{
				wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to accept WebSocket connection.");
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				context.Response.Close();
				continue;
			}

			_ = Task.Run(
				() => HandleWebSocketAsync(wsContext.WebSocket, context.Request.RemoteEndPoint, cancellationToken),
				CancellationToken.None);
		}
	}

	private async Task HandleWebSocketAsync(WebSocket socket, EndPoint? remoteEndPoint,
		CancellationToken cancellationToken)
	{
		var sessionId = Guid.NewGuid().ToString("N");
		var connection = new WebSocketSessionConnection(socket);
		var metadata = new SessionMetadata(sessionId, "websocket", remoteEndPoint, DateTimeOffset.UtcNow);
		var buffer = new byte[_options.ReceiveBufferBytes];
		var maxBytes = _options.MaxMessageBytes;
		using var accumulator = new MemoryStream();

		async ValueTask<InboundFrame> ReadFrameAsync(CancellationToken token)
		{
			while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
			{
				var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					return new InboundFrame(null, SessionCloseReason.ClientDisconnected, socket.CloseStatusDescription);
				}

				if (result.MessageType != WebSocketMessageType.Binary &&
					result.MessageType != WebSocketMessageType.Text)
				{
					continue;
				}

				if (accumulator.Length + result.Count > maxBytes)
				{
					return new InboundFrame(null, SessionCloseReason.ProtocolViolation, "WebSocket frame exceeded maximum size.");
				}

				if (result.Count > 0)
				{
					accumulator.Write(buffer, 0, result.Count);
				}

				if (result.EndOfMessage)
				{
					var payload = accumulator.ToArray();
					accumulator.SetLength(0);
					return new InboundFrame(payload, SessionCloseReason.ClientDisconnected, null);
				}
			}

			return new InboundFrame(null, SessionCloseReason.ClientDisconnected, socket.CloseStatusDescription);
		}

		await RunSessionAsync(connection, metadata, ReadFrameAsync, cancellationToken).ConfigureAwait(false);
	}

	private async Task RunSessionAsync(ISessionConnection connection, SessionMetadata metadata,
		Func<CancellationToken, ValueTask<InboundFrame>> readFrameAsync, CancellationToken cancellationToken)
	{
		var pipeline = CreatePipeline();
		var lifetime = new SessionLifetimeState();
		using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		SessionCloseReason closeReason = SessionCloseReason.ClientDisconnected;
		string? description = null;

		try
		{
			if (!pipeline.RequiresHandshake && !await RunAuthenticationAsync(metadata, null, cancellationToken)
					.ConfigureAwait(false))
			{
				closeReason = SessionCloseReason.ProtocolViolation;
				description = GateHandshakeErrors.AuthRejected;
				_logger.LogInformation("Session {SessionId} rejected by the authentication callback.", metadata.SessionId);
				return;
			}

			if (!pipeline.RequiresHandshake)
			{
				// Plaintext mode keeps the legacy flow: the session actor exists from the moment the
				// connection is accepted and payloads are dispatched without decryption.
				lifetime.Runtime = await CreateSessionRuntimeAsync(connection, metadata, cancellationToken)
					.ConfigureAwait(false);
				_sessions[metadata.SessionId] = lifetime.Runtime;
				lifetime.IdleTask = MonitorIdleAsync(lifetime.Runtime, idleCts.Token);
			}
			else
			{
				StartHandshakeWatchdog(connection, pipeline);
			}

			while (!cancellationToken.IsCancellationRequested)
			{
				var frame = await readFrameAsync(cancellationToken).ConfigureAwait(false);
				if (frame.Payload is null)
				{
					closeReason = frame.EndReason;
					description = frame.Description;
					break;
				}

				connection.MarkActivity();

				if (!pipeline.IsReadyForBusiness)
				{
					if (await ProcessHandshakeAsync(pipeline, frame.Payload, connection, metadata, lifetime,
							idleCts.Token, cancellationToken).ConfigureAwait(false))
					{
						continue;
					}

					closeReason = SessionCloseReason.ProtocolViolation;
					break;
				}

				byte[] plainPayload;
				try
				{
					plainPayload = pipeline.DecryptInbound(frame.Payload);
				}
				catch (Exception ex) when (ex is CryptographicException or ArgumentException)
				{
					closeReason = SessionCloseReason.ProtocolViolation;
					description = $"{GateHandshakeErrors.SessionDecryptFailed}: {ex.Message}";
					_logger.LogWarning("Session {SessionId} closed: {Description}.", metadata.SessionId, description);
					break;
				}

				await lifetime.Runtime!.Actor.SendAsync(new SessionInboundMessage(plainPayload), cancellationToken)
					.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			closeReason = SessionCloseReason.ServerShutdown;
		}
		catch (Exception ex)
		{
			closeReason = SessionCloseReason.TransportError;
			description = ex.Message;
			_logger.LogWarning(ex, "Session {SessionId} ended due to transport error.", metadata.SessionId);
		}
		finally
		{
			if (lifetime.Runtime is not null)
			{
				_sessions.TryRemove(metadata.SessionId, out _);
			}

			try
			{
				await connection.CloseAsync(closeReason, description, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Closing session {SessionId} failed.", metadata.SessionId);
			}

			if (lifetime.Runtime is not null)
			{
				await NotifySessionClosedAsync(lifetime.Runtime, closeReason, description).ConfigureAwait(false);
			}
			else
			{
				// No session actor owns the connection (handshake never completed), so dispose it here.
				await connection.DisposeAsync().ConfigureAwait(false);
			}

			if (lifetime.IdleTask is not null)
			{
				await idleCts.CancelAsync().ConfigureAwait(false);
				await SafeAwaitAsync(lifetime.IdleTask, metadata.SessionId).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Mutable per-connection lifetime state shared between the session runner and the handshake step.</summary>
	private sealed class SessionLifetimeState
	{
		public SessionRuntime? Runtime
		{
			get;
			set;
		}

		public Task? IdleTask
		{
			get;
			set;
		}
	}

	/// <summary>
	/// Processes one frame while the handshake is pending. Returns false when the connection must close
	/// (handshake failure, authentication rejection or timeout-triggered disconnect).
	/// </summary>
	private async Task<bool> ProcessHandshakeAsync(GateSessionPipeline pipeline, byte[] payload,
		ISessionConnection connection, SessionMetadata metadata, SessionLifetimeState lifetime,
		CancellationToken idleToken, CancellationToken cancellationToken)
	{
		GateHandshakeStep step;
		try
		{
			step = pipeline.ProcessHandshakeFrame(payload);
		}
		catch (GateHandshakeException ex)
		{
			await TrySendErrorFrameAsync(connection, ex.Code).ConfigureAwait(false);
			_logger.LogWarning("Session {SessionId} failed the encryption handshake: {Code} {Message}.",
				metadata.SessionId, ex.Code, ex.Message);
			return false;
		}

		if (step.Response is not null)
		{
			await connection.SendAsync(step.Response, cancellationToken).ConfigureAwait(false);
			return true;
		}

		if (!await RunAuthenticationAsync(metadata, step.SessionKey, cancellationToken).ConfigureAwait(false))
		{
			await TrySendErrorFrameAsync(connection, GateHandshakeErrors.AuthRejected).ConfigureAwait(false);
			_logger.LogInformation("Session {SessionId} rejected by the authentication callback.", metadata.SessionId);
			return false;
		}

		pipeline.Activate(step.SessionKey!);
		var secureConnection = new EncryptedSessionConnection(connection, pipeline.Cipher!);
		lifetime.Runtime = await CreateSessionRuntimeAsync(secureConnection, metadata, cancellationToken).ConfigureAwait(false);
		_sessions[metadata.SessionId] = lifetime.Runtime;
		lifetime.IdleTask = MonitorIdleAsync(lifetime.Runtime, idleToken);
		await connection.SendAsync(pipeline.BuildConfirmAck(), CancellationToken.None).ConfigureAwait(false);
		_logger.LogInformation("Session {SessionId} completed the encryption handshake.", metadata.SessionId);
		return true;
	}

	private async ValueTask<bool> RunAuthenticationAsync(SessionMetadata metadata, byte[]? sessionKey,
		CancellationToken cancellationToken)
	{
		var callback = _options.AuthCallback;
		if (callback is null)
		{
			return true;
		}

		return await callback(new GateAuthenticationContext(metadata, sessionKey), cancellationToken)
			.ConfigureAwait(false);
	}

	private void StartHandshakeWatchdog(ISessionConnection connection, GateSessionPipeline pipeline)
	{
		var timeout = _options.HandshakeTimeout;
		_ = Task.Run(
			async () =>
			{
				try
				{
					await Task.Delay(timeout).ConfigureAwait(false);
					if (!pipeline.IsReadyForBusiness)
					{
						await connection.CloseAsync(
							SessionCloseReason.ProtocolViolation,
							$"{GateHandshakeErrors.Timeout}: handshake did not complete within {timeout}.",
							CancellationToken.None).ConfigureAwait(false);
					}
				}
				catch
				{
					// Watchdog failures are irrelevant: the connection is already gone or closing.
				}
			},
			CancellationToken.None);
	}

	private async ValueTask TrySendErrorFrameAsync(ISessionConnection connection, string errorCode)
	{
		try
		{
			await connection.SendAsync(GateFrameCodec.EncodeErrorFrame(errorCode), CancellationToken.None)
				.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to deliver handshake error frame {ErrorCode}.", errorCode);
		}
	}

	private GateSessionPipeline CreatePipeline()
	{
		if (!_options.EnableEncryption)
		{
			return GateSessionPipeline.CreatePlaintext();
		}

		var keyProvider = _options.RsaKeyProvider ?? _generatedKeyProvider
			?? throw new InvalidOperationException("Gate encryption is enabled but no RSA key provider is available.");
		var handshake = new GateEncryptionServerSession(
			keyProvider,
			tokenLifetime: _options.TokenLifetime,
			hkdfSalt: _options.HkdfSalt,
			cipherId: _options.SessionCipherId);
		return GateSessionPipeline.CreateEncrypted(handshake, _options.SessionCipherId);
	}

	private async Task<SessionRuntime> CreateSessionRuntimeAsync(ISessionConnection connection,
		SessionMetadata metadata, CancellationToken cancellationToken)
	{
		var actor = await _system
			.CreateActorAsync(() => new SessionActor(connection, metadata, _options.RouterFactory!),
				cancellationToken: cancellationToken).ConfigureAwait(false);
		return new SessionRuntime(metadata, actor, connection);
	}

	private async Task SafeAwaitAsync(Task task, string sessionId)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Background monitor for session {SessionId} ended with error.", sessionId);
		}
	}

	private async Task NotifySessionClosedAsync(SessionRuntime runtime, SessionCloseReason reason, string? description)
	{
		try
		{
			await runtime.Actor.SendAsync(new SessionClientClosedMessage(reason, description)).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to notify session actor {SessionId} about closure.",
				runtime.Metadata.SessionId);
		}
	}

	private static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "/";
		}

		return path.EndsWith('/') ? path : path + "/";
	}

	private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer,
		CancellationToken cancellationToken)
	{
		var total = 0;
		while (total < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				return false;
			}

			total += read;
		}

		return true;
	}

	private readonly record struct InboundFrame(byte[]? Payload, SessionCloseReason EndReason, string? Description);

	private sealed class SessionRuntime
	{
		internal SessionRuntime(SessionMetadata metadata, ActorRef actor, ISessionConnection connection)
		{
			Metadata = metadata;
			Actor = actor;
			Connection = connection;
		}

		public SessionMetadata Metadata
		{
			get;
		}

		public ActorRef Actor
		{
			get;
		}

		public ISessionConnection Connection
		{
			get;
		}
	}
}
