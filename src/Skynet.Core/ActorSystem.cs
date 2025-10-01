using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Core;

/// <summary>
/// Coordinates actor creation, routing and lifecycle management.
/// </summary>
public sealed class ActorSystem : IAsyncDisposable
{
	private readonly ConcurrentDictionary<long, ActorHost> _actors = new();
	private readonly ConcurrentDictionary<string, ActorHandle> _nameToHandle = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<long, string> _handleToName = new();
	private readonly Lock _registryLock = new();
	private readonly ILoggerFactory _loggerFactory;
	private readonly ITransport _transport;
	private readonly bool _ownsTransport;
	private readonly long _handleOffset;
	private readonly IClusterRegistry? _clusterRegistry;
	private long _nextHandle;
	private long _nextMessageId;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ActorSystem"/> class.
	/// </summary>
	/// <param name="loggerFactory">Factory used to create per-actor loggers.</param>
	/// <param name="transport">Optional transport implementation. If not provided the in-process transport is used.</param>
	/// <param name="inProcOptions">Options applied when the in-process transport is created internally.</param>
	/// <param name="options">Additional actor system configuration.</param>
	/// <param name="transportFactory">Factory used to create a transport instance when <paramref name="transport"/> is not supplied.</param>
	public ActorSystem(ILoggerFactory? loggerFactory = null, ITransport? transport = null,
		InProcTransportOptions? inProcOptions = null, ActorSystemOptions? options = null,
		Func<ActorSystem, ITransport>? transportFactory = null)
	{
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_handleOffset = options?.HandleOffset ?? 0;
		_clusterRegistry = options?.ClusterRegistry;
		Metrics = options?.MetricsCollector ?? new ActorMetricsCollector();

		if (transport is not null)
		{
			_transport = transport;
		}
		else
		{
			_transport = transportFactory is not null
				? transportFactory(this)
				: new InProcTransport(this, inProcOptions);
			_ownsTransport = true;
		}
	}

	/// <summary>
	/// Gets the metrics collector used by the actor system.
	/// </summary>
	public ActorMetricsCollector Metrics { get; }

	/// <summary>
	/// Creates a new actor instance and registers it with the system.
	/// </summary>
	/// <typeparam name="TActor">The type of actor to create.</typeparam>
	/// <param name="factory">Factory used to instantiate the actor.</param>
	/// <param name="name">Optional logical name for the actor.</param>
	/// <param name="creationOptions">Additional options that influence creation.</param>
	/// <param name="cancellationToken">Token used to cancel the creation.</param>
	/// <returns>A reference to the newly created actor.</returns>
	public async Task<ActorRef> CreateActorAsync<TActor>(Func<TActor> factory, string? name = null,
		ActorCreationOptions? creationOptions = null, CancellationToken cancellationToken = default)
		where TActor : Actor
	{
		ArgumentNullException.ThrowIfNull(factory);
		ThrowIfDisposed();

		var actor = factory() ?? throw new InvalidOperationException("The actor factory returned null.");
		var handleValue = creationOptions?.HandleOverride?.Value ??
		                  _handleOffset + Interlocked.Increment(ref _nextHandle);
		var handle = new ActorHandle(handleValue);
		if (!handle.IsValid)
		{
			throw new InvalidOperationException("The actor handle must be greater than zero.");
		}

		var actorName = string.IsNullOrWhiteSpace(name) ? null : name;
		var logger = _loggerFactory.CreateLogger(actor.GetType());
		var host = new ActorHost(this, handle, actor, logger, actorName, Metrics);

		lock (_registryLock)
		{
			if (!_actors.TryAdd(handle.Value, host))
			{
				throw new InvalidOperationException($"Actor handle {handle.Value} is already registered.");
			}

			if (actorName is not null)
			{
				if (_nameToHandle.ContainsKey(actorName))
				{
					_actors.TryRemove(handle.Value, out _);
					throw new InvalidOperationException($"The actor name '{actorName}' is already registered.");
				}

				_nameToHandle[actorName] = handle;
				_handleToName[handle.Value] = actorName;
			}
		}

		var registeredWithRegistry = false;
		if (actorName is not null && _clusterRegistry is not null)
		{
			try
			{
				_clusterRegistry.RegisterLocalActor(actorName, handle);
				registeredWithRegistry = true;
			}
			catch
			{
				await RemoveActorAsync(handle, notifyRegistry: false).ConfigureAwait(false);
				throw;
			}
		}

		try
		{
			await host.Startup.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await RemoveActorAsync(handle, registeredWithRegistry).ConfigureAwait(false);
			throw;
		}

		return new ActorRef(this, handle);
	}

	/// <summary>
	/// Retrieves an actor reference by handle.
	/// </summary>
	public ActorRef GetRef(ActorHandle handle)
	{
		ThrowIfDisposed();
		if (!handle.IsValid)
		{
			throw new ArgumentException("The provided handle is invalid.", nameof(handle));
		}

		if (!_actors.ContainsKey(handle.Value))
		{
			throw new KeyNotFoundException($"Actor with handle {handle.Value} does not exist.");
		}

		return new ActorRef(this, handle);
	}

	/// <summary>
	/// Retrieves an actor reference by name.
	/// </summary>
	public ActorRef GetByName(string name)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		ThrowIfDisposed();

		if (_nameToHandle.TryGetValue(name, out var handle))
		{
			return new ActorRef(this, handle);
		}

		if (_clusterRegistry is not null && _clusterRegistry.TryResolveByName(name, out var location))
		{
			return new ActorRef(this, location.Handle);
		}

		throw new KeyNotFoundException($"Actor '{name}' does not exist.");
	}

	/// <summary>
	/// Creates a proxy instance for the specified service.
	/// </summary>
	public TContract GetService<TContract>(string name, MessagePackSerializerOptions? options = null)
		where TContract : class
	{
		var reference = GetByName(name);
		return reference.CreateProxy<TContract>(options);
	}

	/// <summary>
	/// Attempts to retrieve a handle by name.
	/// </summary>
	public bool TryGetHandleByName(string name, out ActorHandle handle)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		return _nameToHandle.TryGetValue(name, out handle);
	}

	/// <summary>
	/// Creates or retrieves a unique service registered with the specified name.
	/// </summary>
	public async Task<ActorRef> GetOrCreateUniqueAsync<TActor>(string name, Func<TActor> factory,
		CancellationToken cancellationToken = default)
		where TActor : Actor
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(factory);

		if (_nameToHandle.TryGetValue(name, out var existing))
		{
			return new ActorRef(this, existing);
		}

		try
		{
			return await CreateActorAsync(factory, name, null, cancellationToken).ConfigureAwait(false);
		}
		catch (InvalidOperationException) when (_nameToHandle.TryGetValue(name, out existing))
		{
			return new ActorRef(this, existing);
		}
	}

	/// <summary>
	/// Sends a fire-and-forget message to the specified actor.
	/// </summary>
	public ValueTask SendAsync(ActorHandle to, object payload, ActorHandle? from = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(payload);
		ThrowIfDisposed();

		var envelope = CreateEnvelope(to, from ?? ActorHandle.None, CallType.Send, payload);
		return RouteAsync(envelope, null, cancellationToken);
	}

	/// <summary>
	/// Performs a request-response invocation against the specified actor.
	/// </summary>
	public async Task<TResponse> CallAsync<TResponse>(ActorHandle to, object payload, TimeSpan? timeout = null,
		ActorHandle? from = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(payload);
		ThrowIfDisposed();

		var response = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationTokenSource? timeoutSource = null;
		CancellationToken effectiveToken = cancellationToken;
		if (timeout.HasValue)
		{
			timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(timeout.Value);
			effectiveToken = timeoutSource.Token;
		}

		CancellationTokenRegistration registration = default;
		if (effectiveToken.CanBeCanceled)
		{
			registration = effectiveToken.Register(static state =>
			{
				var source = (TaskCompletionSource<object?>)state!;
				source.TrySetCanceled();
			}, response);
		}

		try
		{
			var envelope = CreateEnvelope(to, from ?? ActorHandle.None, CallType.Call, payload);
			await RouteAsync(envelope, response, effectiveToken).ConfigureAwait(false);
			var result = await response.Task.ConfigureAwait(false);
			return (TResponse)result!;
		}
		finally
		{
			registration.Dispose();
			timeoutSource?.Dispose();
		}
	}

	/// <summary>
	/// Stops an actor and removes it from the system.
	/// </summary>
	public async Task<bool> KillAsync(ActorHandle handle)
	{
		ThrowIfDisposed();
		if (!handle.IsValid)
		{
			return false;
		}

		return await RemoveActorAsync(handle).ConfigureAwait(false);
	}

	/// <summary>
	/// Lists the actors currently registered in the system.
	/// </summary>
	public IReadOnlyCollection<ActorDescriptor> ListActors()
	{
		var snapshot = _actors.ToArray();
		var result = new List<ActorDescriptor>(snapshot.Length);
		foreach (var pair in snapshot)
		{
			_handleToName.TryGetValue(pair.Key, out var name);
			result.Add(new ActorDescriptor(new ActorHandle(pair.Key), name, pair.Value.Actor.GetType()));
		}

		return result;
	}

	internal async ValueTask DeliverLocalAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response,
		CancellationToken cancellationToken)
	{
		if (!_actors.TryGetValue(envelope.To.Value, out var host))
		{
			var exception = new InvalidOperationException($"Actor with handle {envelope.To.Value} was not found.");
			response?.TrySetException(exception);
			throw exception;
		}

		await host.Startup.WaitAsync(cancellationToken).ConfigureAwait(false);
		await host.EnqueueAsync(new MailboxMessage(envelope, response), cancellationToken).ConfigureAwait(false);
	}

	internal bool TryGetActorHost(ActorHandle handle, [MaybeNullWhen(false)] out ActorHost host)
	{
		return _actors.TryGetValue(handle.Value, out host);
	}

	private ValueTask RouteAsync(MessageEnvelope envelope, TaskCompletionSource<object?>? response,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return _transport.SendAsync(envelope, response, cancellationToken);
	}

	private MessageEnvelope CreateEnvelope(ActorHandle to, ActorHandle from, CallType callType, object payload)
	{
		var messageId = Interlocked.Increment(ref _nextMessageId);
		var traceId = TraceContext.CurrentTraceId ?? TraceContext.EnsureTraceId();
		return new MessageEnvelope(
			messageId,
			from,
			to,
			callType,
			payload,
			traceId,
			DateTimeOffset.UtcNow,
			TimeToLive: null,
			Version: 1);
	}

	private async ValueTask<bool> RemoveActorAsync(ActorHandle handle, bool notifyRegistry = true)
	{
		if (!_actors.TryRemove(handle.Value, out var host))
		{
			return false;
		}

		string? name = null;
		if (_handleToName.TryRemove(handle.Value, out name))
		{
			_nameToHandle.TryRemove(name, out _);
			if (notifyRegistry && _clusterRegistry is not null)
			{
				_clusterRegistry.UnregisterLocalActor(name, handle);
			}
		}

		Metrics.UnregisterActor(handle);
		await host.DisposeAsync().ConfigureAwait(false);
		return true;
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		var snapshot = _actors.Keys.ToArray();
		foreach (var handleValue in snapshot)
		{
			await RemoveActorAsync(new ActorHandle(handleValue)).ConfigureAwait(false);
		}

		if (_ownsTransport)
		{
			switch (_transport)
			{
				case IAsyncDisposable asyncDisposable:
					await asyncDisposable.DisposeAsync().ConfigureAwait(false);
					break;
				case IDisposable disposable:
					disposable.Dispose();
					break;
			}
		}

		if (_clusterRegistry is IAsyncDisposable asyncRegistry)
		{
			await asyncRegistry.DisposeAsync().ConfigureAwait(false);
		}
		else if (_clusterRegistry is IDisposable registryDisposable)
		{
			registryDisposable.Dispose();
		}
	}
}

/// <summary>
/// Describes an actor registered in the system.
/// </summary>
public sealed record ActorDescriptor(ActorHandle Handle, string? Name, Type ImplementationType);
