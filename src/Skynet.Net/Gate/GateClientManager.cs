using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Net;

/// <summary>
/// Pool of <see cref="GateClient"/> instances, one per outbound Gate endpoint. Owns round-robin
/// selection across the live pool, idempotent Add/Update/Remove of endpoints discovered at
/// runtime, and coordinated start/stop. The pool is session-level (a service fanning out across
/// multiple Gate processes) and deliberately independent of <c>Skynet.Cluster</c>'s per-node
/// routing transport, which solves a different problem.
/// </summary>
public sealed class GateClientManager
{
	private readonly object _sync = new();
	private readonly IGateClientTransport _transport;
	private readonly ReconnectPolicy _reconnectPolicy;
	private readonly IReconnectDelayStrategy _delayStrategy;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _logger;
	private readonly List<GateClient> _clients = [];
	private readonly string _namePrefix;
	private uint _roundRobin;
	private bool _started;

	/// <param name="endpoints">Static gate addresses ("host:port"); auto-named "Gate0", "Gate1", ...</param>
	/// <param name="transport">Outbound transport used to open connections.</param>
	/// <param name="reconnectPolicy">Backoff policy; defaults to no retries.</param>
	/// <param name="delayStrategy">Reconnect delay strategy; defaults to real timers. Inject a fake for tests.</param>
	/// <param name="loggerFactory">Logging for the pool and its clients.</param>
	/// <param name="namePrefix">Prefix for auto-generated endpoint names.</param>
	public GateClientManager(
		IReadOnlyList<string> endpoints,
		IGateClientTransport transport,
		ReconnectPolicy? reconnectPolicy = null,
		IReconnectDelayStrategy? delayStrategy = null,
		ILoggerFactory? loggerFactory = null,
		string namePrefix = "Gate")
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_reconnectPolicy = reconnectPolicy ?? new ReconnectPolicy(maxAttempts: 0);
		_delayStrategy = delayStrategy ?? TimerReconnectDelayStrategy.Instance;
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_logger = _loggerFactory.CreateLogger("Skynet.Net.GateClientManager");
		_namePrefix = string.IsNullOrWhiteSpace(namePrefix) ? "Gate" : namePrefix;

		foreach (var endpoint in BuildEndpoints(endpoints))
		{
			_clients.Add(CreateClient(endpoint));
		}
	}

	public IReadOnlyList<GateClient> Clients
	{
		get
		{
			lock (_sync)
			{
				return _clients.ToArray();
			}
		}
	}

	public IReadOnlyList<GateProxy> Proxies
	{
		get
		{
			lock (_sync)
			{
				return _clients.Select(client => client.Proxy).ToArray();
			}
		}
	}

	/// <summary>
	/// Selects a connected <see cref="GateProxy"/> using round-robin over the live pool, or null
	/// when no Gate is currently connected. Sweeps from a rotating cursor so consecutive calls fan
	/// out across connected Gates; never loops forever when everything is down.
	/// </summary>
	public GateProxy? SelectConnectedProxy()
	{
		lock (_sync)
		{
			var count = _clients.Count;
			if (count == 0)
			{
				return null;
			}

			for (var i = 0; i < count; i++)
			{
				var index = (int)((_roundRobin + (uint)i) % (uint)count);
				var proxy = _clients[index].Proxy;
				if (proxy.IsConnected)
				{
					_roundRobin = (uint)(index + 1);
					return proxy;
				}
			}

			return null;
		}
	}

	/// <summary>
	/// Sends a frame through a connected Gate selected by <see cref="SelectConnectedProxy"/>.
	/// Throws <see cref="InvalidOperationException"/> when no Gate is connected.
	/// </summary>
	public Task SendThroughAnyAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
	{
		var proxy = SelectConnectedProxy()
			?? throw new InvalidOperationException("No connected Gate is available to send through.");
		return proxy.SendAsync(frame, cancellationToken);
	}

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		List<GateClient> clients;
		lock (_sync)
		{
			if (_started)
			{
				return;
			}

			_started = true;
			clients = [.. _clients];
		}

		try
		{
			// Each client returns as soon as its first connect attempt is scheduled or fails into
			// the reconnect loop, so an unreachable endpoint cannot block startup of the others.
			await Task.WhenAll(clients.Select(client => client.StartAsync(cancellationToken))).ConfigureAwait(false);
			_logger.LogInformation("Gate client manager started with {Count} endpoints.", clients.Count);
		}
		catch
		{
			await StopAsync(CancellationToken.None).ConfigureAwait(false);
			throw;
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		List<GateClient> clients;
		lock (_sync)
		{
			if (!_started)
			{
				return;
			}

			_started = false;
			clients = [.. _clients];
		}

		await Task.WhenAll(clients.Select(client => client.StopAsync(cancellationToken))).ConfigureAwait(false);
		_logger.LogInformation("Gate client manager stopped.");
	}

	/// <summary>
	/// Adds a Gate endpoint discovered at runtime and starts its connection loop when the manager
	/// is running. Re-adding an endpoint with the same name and address is a no-op; a changed
	/// address routes through <see cref="UpdateGateAsync"/> so the stale connection is replaced.
	/// </summary>
	public Task AddGateAsync(GateEndpoint endpoint, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		if (string.IsNullOrWhiteSpace(endpoint.Address))
		{
			throw new InvalidOperationException("Gate endpoint address cannot be empty.");
		}

		GateClient newClient;
		bool startNow;
		lock (_sync)
		{
			if (TryFindClient(endpoint.Name, out var existing))
			{
				if (string.Equals(existing.Endpoint.Address, endpoint.Address, StringComparison.Ordinal))
				{
					return Task.CompletedTask;
				}

				// Address changed; fall through to the update path which replaces the client.
				return UpdateGateAsync(endpoint, cancellationToken);
			}

			newClient = CreateClient(endpoint);
			_clients.Add(newClient);
			startNow = _started;
		}

		_logger.LogInformation("Gate endpoint {EndpointName} added at {Address}.", endpoint.Name, endpoint.Address);
		return startNow ? newClient.StartAsync(cancellationToken) : Task.CompletedTask;
	}

	/// <summary>
	/// Updates a previously added Gate endpoint. When the address is unchanged the call is a
	/// no-op; otherwise the existing client is stopped and replaced with one targeting the new
	/// address (service moved, rescheduled, redeployed...).
	/// </summary>
	public async Task UpdateGateAsync(GateEndpoint endpoint, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		if (string.IsNullOrWhiteSpace(endpoint.Address))
		{
			throw new InvalidOperationException("Gate endpoint address cannot be empty.");
		}

		GateClient? oldClient = null;
		GateClient? newClient = null;
		bool startNow = false;
		lock (_sync)
		{
			if (TryFindClient(endpoint.Name, out var existing))
			{
				if (string.Equals(existing.Endpoint.Address, endpoint.Address, StringComparison.Ordinal))
				{
					return;
				}

				oldClient = existing;
				_clients.Remove(existing);
			}

			newClient = CreateClient(endpoint);
			_clients.Add(newClient);
			startNow = _started;
		}

		_logger.LogInformation("Gate endpoint {EndpointName} updated to {Address}.", endpoint.Name, endpoint.Address);

		if (oldClient is not null)
		{
			await oldClient.StopAsync(cancellationToken).ConfigureAwait(false);
		}

		if (startNow && newClient is not null)
		{
			await newClient.StartAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Removes a Gate endpoint and stops its connection loop. Unknown names are ignored.
	/// </summary>
	public async Task RemoveGateAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		GateClient? client = null;
		lock (_sync)
		{
			if (TryFindClient(name, out var existing))
			{
				client = existing;
				_clients.Remove(existing);
			}
		}

		if (client is not null)
		{
			_logger.LogInformation("Gate endpoint {EndpointName} removed.", name);
			await client.StopAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private GateClient CreateClient(GateEndpoint endpoint)
		=> new(endpoint, _transport, _reconnectPolicy, _delayStrategy,
			_loggerFactory.CreateLogger("Skynet.Net.GateClient"));

	private bool TryFindClient(string name, out GateClient client)
	{
		foreach (var candidate in _clients)
		{
			if (string.Equals(candidate.Endpoint.Name, name, StringComparison.Ordinal))
			{
				client = candidate;
				return true;
			}
		}

		client = null!;
		return false;
	}

	private IEnumerable<GateEndpoint> BuildEndpoints(IReadOnlyList<string> endpoints)
	{
		for (var index = 0; index < endpoints.Count; index++)
		{
			var address = endpoints[index];
			if (string.IsNullOrWhiteSpace(address))
			{
				throw new InvalidOperationException("Gate endpoint address cannot be empty.");
			}

			yield return new GateEndpoint($"{_namePrefix}{index}", address);
		}
	}
}
