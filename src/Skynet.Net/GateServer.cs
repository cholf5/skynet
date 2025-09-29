using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;

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
	public IPEndPoint? TcpEndpoint { get; private set; }

	/// <summary>
	/// Gets the public WebSocket URI clients should connect to.
	/// </summary>
	public Uri? WebSocketEndpoint { get; private set; }

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

		cts.Cancel();
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
				await session.Actor.SendAsync(new SessionCloseMessage(SessionCloseReason.ServerShutdown, "Gate server stopping"), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to notify session {SessionId} about shutdown.", session.Metadata.SessionId);
			}
		}

		_sessions.Clear();
		_webSocketListener?.Close();
		_webSocketListener = null;
		WebSocketEndpoint = null;

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
			TcpClient? client = null;
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

			if (client is not null)
			{
				_ = Task.Run(() => HandleTcpClientAsync(client, cancellationToken), CancellationToken.None);
			}
		}
	}

	private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		var sessionId = Guid.NewGuid().ToString("N");
		var connection = new TcpSessionConnection(client);
		var metadata = new SessionMetadata(sessionId, "tcp", client.Client.RemoteEndPoint, DateTimeOffset.UtcNow);
		SessionRuntime? runtime = null;
		using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var sessionToken = sessionCts.Token;

		try
		{
			runtime = await CreateSessionRuntimeAsync(connection, metadata, sessionToken).ConfigureAwait(false);
			_sessions[sessionId] = runtime;
			var receiveTask = RunTcpReceiveLoopAsync(runtime, client.GetStream(), sessionToken);
			var idleTask = MonitorIdleAsync(runtime, sessionToken);
			await receiveTask.ConfigureAwait(false);
			sessionCts.Cancel();
			await SafeAwaitAsync(idleTask, sessionId).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "TCP session {SessionId} terminated unexpectedly.", sessionId);
		}
		finally
		{
			if (runtime is not null)
			{
				_sessions.TryRemove(runtime.Metadata.SessionId, out _);
			}
		}
	}

	private async Task RunTcpReceiveLoopAsync(SessionRuntime runtime, NetworkStream stream, CancellationToken cancellationToken)
	{
		var header = new byte[4];
		var maxBytes = _options.MaxMessageBytes;
		SessionCloseReason closeReason = SessionCloseReason.ClientDisconnected;
		string? description = null;

		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
				{
					break;
				}

				var length = BinaryPrimitives.ReadInt32BigEndian(header);
				if (length < 0 || length > maxBytes)
				{
					closeReason = SessionCloseReason.ProtocolViolation;
					description = $"Invalid message length {length}.";
					break;
				}

				var payload = new byte[length];
				if (length > 0 && !await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
				{
					break;
				}

				runtime.Connection.MarkActivity();
				await runtime.Actor.SendAsync(new SessionInboundMessage(payload)).ConfigureAwait(false);
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
			_logger.LogWarning(ex, "TCP session {SessionId} ended due to transport error.", runtime.Metadata.SessionId);
		}
		finally
		{
			try
			{
				await runtime.Connection.CloseAsync(closeReason, description, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Closing TCP session {SessionId} failed.", runtime.Metadata.SessionId);
			}
			await NotifySessionClosedAsync(runtime, closeReason, description).ConfigureAwait(false);
		}
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
					await runtime.Actor.SendAsync(new SessionHeartbeatTimeoutMessage(timeout), CancellationToken.None).ConfigureAwait(false);
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
			HttpListenerContext? context = null;
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

			if (context is null)
			{
				continue;
			}

			if (!context.Request.IsWebSocketRequest)
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				context.Response.Close();
				continue;
			}

			WebSocketContext? wsContext = null;
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

			_ = Task.Run(() => HandleWebSocketAsync(wsContext.WebSocket, context.Request.RemoteEndPoint, cancellationToken), CancellationToken.None);
		}
	}

	private async Task HandleWebSocketAsync(WebSocket socket, EndPoint? remoteEndPoint, CancellationToken cancellationToken)
	{
		var sessionId = Guid.NewGuid().ToString("N");
		var connection = new WebSocketSessionConnection(socket);
		var metadata = new SessionMetadata(sessionId, "websocket", remoteEndPoint, DateTimeOffset.UtcNow);
		SessionRuntime? runtime = null;
		using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var sessionToken = sessionCts.Token;

		try
		{
			runtime = await CreateSessionRuntimeAsync(connection, metadata, sessionToken).ConfigureAwait(false);
			_sessions[sessionId] = runtime;
			var receiveTask = RunWebSocketReceiveLoopAsync(runtime, socket, sessionToken);
			var idleTask = MonitorIdleAsync(runtime, sessionToken);
			await receiveTask.ConfigureAwait(false);
			sessionCts.Cancel();
			await SafeAwaitAsync(idleTask, sessionId).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "WebSocket session {SessionId} terminated unexpectedly.", sessionId);
		}
		finally
		{
			if (runtime is not null)
			{
				_sessions.TryRemove(runtime.Metadata.SessionId, out _);
			}
		}
	}

	private async Task RunWebSocketReceiveLoopAsync(SessionRuntime runtime, WebSocket socket, CancellationToken cancellationToken)
	{
		var buffer = new byte[_options.ReceiveBufferBytes];
		using var accumulator = new MemoryStream();
		SessionCloseReason reason = SessionCloseReason.ClientDisconnected;
		string? description = null;

		try
		{
			while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
			{
				var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					reason = SessionCloseReason.ClientDisconnected;
					description = socket.CloseStatusDescription;
					break;
				}

				if (result.MessageType != WebSocketMessageType.Binary && result.MessageType != WebSocketMessageType.Text)
				{
					continue;
				}

				if (accumulator.Length + result.Count > _options.MaxMessageBytes)
				{
					reason = SessionCloseReason.ProtocolViolation;
					description = "WebSocket frame exceeded maximum size.";
					break;
				}

				if (result.Count > 0)
				{
					accumulator.Write(buffer, 0, result.Count);
					runtime.Connection.MarkActivity();
				}

				if (result.EndOfMessage)
				{
					var payload = accumulator.ToArray();
					accumulator.SetLength(0);
					await runtime.Actor.SendAsync(new SessionInboundMessage(payload)).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			reason = SessionCloseReason.ServerShutdown;
		}
		catch (WebSocketException ex)
		{
			reason = SessionCloseReason.TransportError;
			description = ex.Message;
			_logger.LogWarning(ex, "WebSocket session {SessionId} ended with error.", runtime.Metadata.SessionId);
		}
		finally
		{
			try
			{
				await runtime.Connection.CloseAsync(reason, description, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Closing WebSocket session {SessionId} failed.", runtime.Metadata.SessionId);
			}
			await NotifySessionClosedAsync(runtime, reason, description).ConfigureAwait(false);
		}
	}

	private async Task<SessionRuntime> CreateSessionRuntimeAsync(ISessionConnection connection, SessionMetadata metadata, CancellationToken cancellationToken)
	{
		var actor = await _system.CreateActorAsync(() => new SessionActor(connection, metadata, _options.RouterFactory!), cancellationToken: cancellationToken).ConfigureAwait(false);
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
			_logger.LogDebug(ex, "Failed to notify session actor {SessionId} about closure.", runtime.Metadata.SessionId);
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

	private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
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

	private sealed class SessionRuntime
	{
		internal SessionRuntime(SessionMetadata metadata, ActorRef actor, ISessionConnection connection)
		{
			Metadata = metadata;
			Actor = actor;
			Connection = connection;
		}

		public SessionMetadata Metadata { get; }
		public ActorRef Actor { get; }
		public ISessionConnection Connection { get; }
	}
}
