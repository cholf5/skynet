using Microsoft.Extensions.Logging;

namespace Skynet.Net;

/// <summary>
/// Maintains one outbound connection to a single Gate endpoint: connects with retries, installs
/// the connection into the endpoint's <see cref="GateProxy"/> (lock-free hot swap), and schedules
/// an exponential-backoff reconnect loop when the remote side drops. Senders are never blocked by
/// a reconnect; they observe the swap only through the proxy.
/// </summary>
public sealed class GateClient
{
	private readonly object _sync = new();
	private readonly IGateClientTransport _transport;
	private readonly ReconnectPolicy _reconnectPolicy;
	private readonly IReconnectDelayStrategy _delayStrategy;
	private readonly ILogger _logger;
	private CancellationTokenSource? _stopCts;
	private Task _connectionTask = Task.CompletedTask;
	private GateClientState _state = GateClientState.Disconnected;
	private Exception? _lastError;

	public GateClient(
		GateEndpoint endpoint,
		IGateClientTransport transport,
		ReconnectPolicy? reconnectPolicy = null,
		IReconnectDelayStrategy? delayStrategy = null,
		ILogger? logger = null)
	{
		Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_reconnectPolicy = reconnectPolicy ?? new ReconnectPolicy(maxAttempts: 0);
		_delayStrategy = delayStrategy ?? TimerReconnectDelayStrategy.Instance;
		_logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
		Proxy = new GateProxy(endpoint);
	}

	public GateEndpoint Endpoint { get; }

	public GateProxy Proxy { get; }

	/// <summary>
	/// Raised after a new connection is installed and the client transitions to
	/// <see cref="GateClientState.Connected"/>. Fires once per successful connect (including each
	/// successful reconnect); triggered outside the state lock so subscriber work cannot reenter.
	/// </summary>
	public event EventHandler<GateClientConnectedEventArgs>? Connected;

	public GateClientState State
	{
		get
		{
			lock (_sync)
			{
				return _state;
			}
		}
	}

	public Exception? LastError
	{
		get
		{
			lock (_sync)
			{
				return _lastError;
			}
		}
	}

	public bool IsConnected => Proxy.IsConnected;

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		lock (_sync)
		{
			if (_state is GateClientState.Connected or GateClientState.Connecting ||
				(_state == GateClientState.Disconnected && _stopCts is not null && !_stopCts.IsCancellationRequested &&
					!_connectionTask.IsCompleted))
			{
				// Already connected, connecting, or a reconnect loop is still in flight.
				return Task.CompletedTask;
			}

			_stopCts?.Dispose();
			_stopCts = new CancellationTokenSource();
			_lastError = null;
			_state = GateClientState.Connecting;
			_connectionTask = RunConnectionLoopAsync(_stopCts.Token);
		}

		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		CancellationTokenSource? stopCts;
		Task connectionTask;
		IGateProxyConnection? connection;

		lock (_sync)
		{
			stopCts = _stopCts;
			_stopCts = null;
			stopCts?.Cancel();
			connectionTask = _connectionTask;
			connection = Proxy.ReplaceConnection(null);
			if (connection is not null)
			{
				connection.Disconnected -= OnDisconnected;
			}

			_state = GateClientState.Stopped;
		}

		if (connection is not null)
		{
			await connection.DisposeAsync().ConfigureAwait(false);
		}

		try
		{
			await connectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (stopCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
		{
		}
		finally
		{
			stopCts?.Dispose();
		}

		_logger.LogInformation("Gate client {EndpointName} stopped.", Endpoint.Name);
	}

	private async Task RunConnectionLoopAsync(CancellationToken stopToken)
	{
		try
		{
			await ConnectWithRetriesAsync(stopToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
		{
		}
	}

	private async Task ConnectWithRetriesAsync(CancellationToken token)
	{
		for (var attempt = 0; ; attempt++)
		{
			token.ThrowIfCancellationRequested();
			SetState(GateClientState.Connecting);

			try
			{
				_logger.LogDebug("Gate client {EndpointName} connecting to {Address}.", Endpoint.Name, Endpoint.Address);
				var connection = await _transport.ConnectAsync(Endpoint, token).ConfigureAwait(false);
				try
				{
					InstallConnection(connection);
				}
				catch
				{
					// A stop raced the connect; dispose immediately so no zombie connection survives.
					await connection.DisposeAsync().ConfigureAwait(false);
					throw;
				}

				return;
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (_reconnectPolicy.TryGetDelay(attempt, out var delay))
			{
				SetDisconnected(ex);
				_logger.LogWarning(ex, "Gate client {EndpointName} connect attempt {Attempt} failed; retrying in {Delay}.",
					Endpoint.Name, attempt + 1, delay);
				await _delayStrategy.DelayAsync(delay, token).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				SetDisconnected(ex);
				_logger.LogError(ex, "Gate client {EndpointName} reconnect budget exhausted; staying disconnected.",
					Endpoint.Name);
				return;
			}
		}
	}

	private void InstallConnection(IGateProxyConnection connection)
	{
		ArgumentNullException.ThrowIfNull(connection);

		IGateProxyConnection? previous;
		lock (_sync)
		{
			if (_stopCts is null || _stopCts.IsCancellationRequested)
			{
				// Stop raced the connect; reject the connection so it cannot linger as a zombie.
				throw new OperationCanceledException("Gate client has been stopped.");
			}

			connection.Disconnected += OnDisconnected;
			previous = Proxy.ReplaceConnection(connection);
			if (previous is not null)
			{
				previous.Disconnected -= OnDisconnected;
			}

			_lastError = null;
			_state = GateClientState.Connected;
		}

		if (previous is not null)
		{
			_ = previous.DisposeAsync();
		}

		_logger.LogInformation("Gate client {EndpointName} connected via {ConnectionId}.",
			Endpoint.Name, connection.ConnectionId);

		// Raise outside the lock so subscriber work (e.g. sending a register frame) does not
		// reenter GateClient state under _sync. If a subscriber throws we still consider the
		// connection installed — the reconnect policy takes over via the Disconnected path
		// if the caller closes the connection.
		Connected?.Invoke(this, new GateClientConnectedEventArgs(connection));
	}

	private void OnDisconnected(object? sender, GateDisconnectedEventArgs e)
	{
		if (sender is not IGateProxyConnection connection)
		{
			return;
		}

		connection.Disconnected -= OnDisconnected;
		_ = connection.DisposeAsync();

		lock (_sync)
		{
			if (_stopCts is null || _stopCts.IsCancellationRequested || !Proxy.TryClearConnection(connection))
			{
				return;
			}

			_logger.LogWarning(e.Exception, "Gate client {EndpointName} disconnected; scheduling reconnect.",
				Endpoint.Name);
			_lastError = e.Exception;
			_state = GateClientState.Disconnected;
			_connectionTask = RunConnectionLoopAsync(_stopCts.Token);
		}
	}

	private void SetDisconnected(Exception exception)
	{
		lock (_sync)
		{
			if (_state != GateClientState.Stopped)
			{
				_lastError = exception;
				_state = GateClientState.Disconnected;
			}
		}
	}

	private void SetState(GateClientState state)
	{
		lock (_sync)
		{
			if (_state != GateClientState.Stopped)
			{
				_state = state;
			}
		}
	}
}
