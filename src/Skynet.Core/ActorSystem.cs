using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core.Persistence;

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
	private readonly ILogger _logger;
	private readonly ITransport _transport;
	private readonly bool _ownsTransport;
	private readonly long _handleOffset;
	private readonly IClusterRegistry? _clusterRegistry;
	private readonly ActorTimerScheduler _timers;
	private IActorSnapshotStore? _snapshotStore;
	private long _nextHandle;
	private long _nextMessageId;
	private bool _disposed;

	/// <summary>
	/// Gets the most recently allocated message id. Introspection hook for tests that need to
	/// reason about cross-node message id alignment (each system allocates from its own counter).
	/// </summary>
	internal long LastAllocatedMessageId => Interlocked.Read(ref _nextMessageId);

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
		_logger = _loggerFactory.CreateLogger<ActorSystem>();
		_handleOffset = options?.HandleOffset ?? 0;
		_clusterRegistry = options?.ClusterRegistry;
		Metrics = options?.MetricsCollector ?? new ActorMetricsCollector();
		_timers = new ActorTimerScheduler(this);

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
	/// Gets the optional snapshot store registered via <see cref="UseSnapshotStore"/>, or
	/// <see langword="null"/> when persistence is not enabled. Until a store is registered the
	/// persistence hooks have no effect on the system: no allocations, no checks on the message
	/// path, identical behavior for every actor.
	/// </summary>
	public IActorSnapshotStore? SnapshotStore => _snapshotStore;

	/// <summary>
	/// Registers the optional persistence plugin. This is the only assembly point for snapshot
	/// persistence: without it, <see cref="SnapshotActorAsync"/> and
	/// <see cref="RestoreActorSnapshotAsync"/> fail fast and actors never perform any I/O.
	/// Registering a store never changes actor behavior on its own — snapshots are only taken or
	/// restored when the caller explicitly invokes the corresponding methods, and only for actors
	/// implementing <see cref="ISnapshotable"/>.
	/// </summary>
	/// <param name="store">The store implementation to use. A previously registered store is replaced.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="store"/> is <see langword="null"/>.</exception>
	/// <exception cref="ObjectDisposedException">Thrown when the system has been disposed.</exception>
	public void UseSnapshotStore(IActorSnapshotStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		ThrowIfDisposed();
		_snapshotStore = store;
	}

	/// <summary>
	/// Raised when a generated void (send) proxy observes that a fire-and-forget enqueue failed
	/// (e.g. the transport was disposed or the enqueue was canceled). The send path never throws
	/// to its caller, so this hook — together with the <see cref="ActorMetricsCollector"/> send
	/// failure counter — is the only way to observe such failures. Handlers may fire concurrently
	/// on the thread pool (each faulted enqueue runs its own OnlyOnFaulted continuation), so
	/// handler implementations must be thread-safe.
	/// </summary>
	public event Action<ActorRef, Exception>? SendEnqueueFailed;

	internal void OnSendEnqueueFailed(ActorRef actor, Exception exception)
	{
		Metrics.OnSendEnqueueFailed();
		// A throwing handler must never escape — this method runs inside the generated proxy's
		// OnlyOnFaulted continuation (a deliberately discarded task), so an escape would resurface
		// as an unobserved task exception, the exact problem this hook exists to eliminate.
		// Handlers are invoked individually so one throwing handler can neither escape nor skip
		// the remaining handlers of the same event.
		if (SendEnqueueFailed is not Action<ActorRef, Exception> handlers)
		{
			return;
		}

		foreach (var handler in handlers.GetInvocationList())
		{
			try
			{
				((Action<ActorRef, Exception>)handler)(actor, exception);
			}
			catch (Exception handlerEx)
			{
				_logger.LogDebug(handlerEx, "A SendEnqueueFailed handler threw while reporting a failed send of type {ExceptionType}.",
					exception.GetType().Name);
			}
		}
	}

	/// <summary>
	/// Gets the timer scheduler that delivers due timer callbacks to actor mailboxes.
	/// </summary>
	internal ActorTimerScheduler Timers => _timers;

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

		// Mailboxes are strictly serial: awaiting a call suspends this actor's mailbox until the
		// callee responds. If the target is already suspended somewhere on this causal call stack,
		// the call can never complete — fail loudly with the full cycle path instead of hanging.
		// This is the single interception point: direct calls, ActorRef.CallAsync and generated
		// RPC proxies all funnel through this method.
		ThrowIfCyclicalCall(to);

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
	/// Explicitly captures and stores a snapshot of the actor's state.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The capture runs <em>on the actor's message loop</em> as a mailbox system callback: it is
	/// serialized with all pending and subsequent messages, so the snapshot reflects a consistent
	/// state with no message interleaved before or during capture, and no actor state is ever read
	/// from an outside thread. The store write is awaited before the actor processes further
	/// messages, which makes the returned snapshot a durable checkpoint of the mailbox position.
	/// </para>
	/// <para>
	/// Restore timing is deliberately explicit: this framework never reads the store (and never
	/// performs any persistence I/O) on its own — neither during actor creation nor in
	/// <see cref="Actor.HandleStartAsync"/>. Applications call
	/// <see cref="RestoreActorSnapshotAsync"/> when they decide a fresh instance should resume
	/// from persisted state (see docs/persistence.md for the rationale).
	/// </para>
	/// </remarks>
	/// <param name="handle">The handle of the actor to snapshot.</param>
	/// <param name="actorKey">The stable persistence key of the actor (survives handle recycling).</param>
	/// <param name="generation">The generation to save under; <see langword="null"/> saves into the unversioned overwrite slot (<see cref="ActorSnapshotKey.Unversioned"/>).</param>
	/// <param name="cancellationToken">Token used to cancel the operation.</param>
	/// <returns>The stored snapshot.</returns>
	/// <exception cref="ArgumentException">Thrown when <paramref name="actorKey"/> is null or empty.</exception>
	/// <exception cref="InvalidOperationException">Thrown when no snapshot store is registered, or the actor does not implement <see cref="ISnapshotable"/>.</exception>
	/// <exception cref="KeyNotFoundException">Thrown when no actor with <paramref name="handle"/> exists.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="generation"/> is negative.</exception>
	public async Task<ActorSnapshot> SnapshotActorAsync(ActorHandle handle, string actorKey, long? generation = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(actorKey);
		ThrowIfDisposed();
		if (generation.HasValue && generation.Value < ActorSnapshotKey.Unversioned)
		{
			throw new ArgumentOutOfRangeException(nameof(generation), "The snapshot generation must not be negative.");
		}

		var store = _snapshotStore ??
			throw new InvalidOperationException("No snapshot store is registered. Call UseSnapshotStore before taking snapshots.");
		var (host, snapshotable) = ResolveSnapshotableActor(handle);
		var effectiveGeneration = generation ?? ActorSnapshotKey.Unversioned;

		var completion = new TaskCompletionSource<ActorSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
		var envelope = CreateEnvelope(handle, ActorHandle.None, CallType.Send, new SnapshotRequested(handle));
		var message = new MailboxMessage(envelope, async token =>
		{
			try
			{
				var payload = await snapshotable.CaptureSnapshotAsync(token).ConfigureAwait(false);
				var snapshot = new ActorSnapshot(actorKey, effectiveGeneration, DateTimeOffset.UtcNow, payload);
				await store.SaveAsync(snapshot, token).ConfigureAwait(false);
				completion.TrySetResult(snapshot);
			}
			catch (Exception exception)
			{
				// Failures (including the actor's own capture errors) surface to the caller of this
				// method; they neither kill the actor nor invoke its error hook, because this is a
				// caller-driven operation rather than regular message processing.
				completion.TrySetException(exception);
			}
		});

		await host.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
		return await AwaitSnapshotHookAsync(completion, host, handle, "snapshot", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Explicitly restores an actor's state from a previously stored snapshot.
	/// </summary>
	/// <remarks>
	/// The store read happens off the actor loop (the mailbox stays free during store I/O); the
	/// payload is then applied on the actor's message loop, so messages the caller sends after
	/// this method returns are guaranteed to observe the restored state. Intended to be called
	/// once, right after the actor instance is created.
	/// </remarks>
	/// <param name="handle">The handle of the actor to restore into.</param>
	/// <param name="actorKey">The stable persistence key under which the snapshot was saved.</param>
	/// <param name="generation">The generation to load; <see langword="null"/> loads the latest available generation.</param>
	/// <param name="cancellationToken">Token used to cancel the operation.</param>
	/// <returns>The snapshot that was applied, or <see langword="null"/> when the store holds no snapshot for the key.</returns>
	/// <exception cref="ArgumentException">Thrown when <paramref name="actorKey"/> is null or empty.</exception>
	/// <exception cref="InvalidOperationException">Thrown when no snapshot store is registered, or the actor does not implement <see cref="ISnapshotable"/>.</exception>
	/// <exception cref="KeyNotFoundException">Thrown when no actor with <paramref name="handle"/> exists.</exception>
	public async Task<ActorSnapshot?> RestoreActorSnapshotAsync(ActorHandle handle, string actorKey, long? generation = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(actorKey);
		ThrowIfDisposed();
		var store = _snapshotStore ??
			throw new InvalidOperationException("No snapshot store is registered. Call UseSnapshotStore before restoring snapshots.");
		var (host, snapshotable) = ResolveSnapshotableActor(handle);

		// Store I/O deliberately happens before the mailbox message is enqueued: the actor loop is
		// not blocked while the store reads, and the payload applied on the loop is already loaded.
		var snapshot = await store.LoadAsync(new ActorSnapshotKey(actorKey, generation), cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
		{
			return null;
		}

		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var envelope = CreateEnvelope(handle, ActorHandle.None, CallType.Send, new SnapshotRestoreRequested(handle));
		var message = new MailboxMessage(envelope, async token =>
		{
			try
			{
				await snapshotable.RestoreSnapshotAsync(snapshot.Payload, token).ConfigureAwait(false);
				completion.TrySetResult(true);
			}
			catch (Exception exception)
			{
				completion.TrySetException(exception);
			}
		});

		await host.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
		await AwaitSnapshotHookAsync(completion, host, handle, "snapshot restore", cancellationToken).ConfigureAwait(false);
		return snapshot;
	}

	/// <summary>
	/// Resolves the host and the <see cref="ISnapshotable"/> view of the target actor, failing fast
	/// with distinct errors when the actor does not exist or has not opted into persistence.
	/// </summary>
	private (ActorHost Host, ISnapshotable Snapshotable) ResolveSnapshotableActor(ActorHandle handle)
	{
		if (!TryGetActorHost(handle, out var host))
		{
			throw new KeyNotFoundException($"Actor with handle {handle.Value} does not exist.");
		}

		if (host.Actor is not ISnapshotable snapshotable)
		{
			throw new InvalidOperationException(
				$"Actor of type '{host.Actor.GetType().FullName}' does not implement ISnapshotable " +
				"and does not participate in snapshot persistence.");
		}

		return (host, snapshotable);
	}

	/// <summary>
	/// Awaits the completion of a snapshot hook callback that runs on the actor's message loop.
	/// Guards against the actor stopping before the queued callback ran (the mailbox drops pending
	/// messages on shutdown), which would otherwise leave the caller awaiting forever.
	/// </summary>
	private static async Task<T> AwaitSnapshotHookAsync<T>(TaskCompletionSource<T> completion, ActorHost host,
		ActorHandle handle, string operation, CancellationToken cancellationToken)
	{
		var finished = await Task.WhenAny(completion.Task, host.Stopped).ConfigureAwait(false);
		if (finished != completion.Task)
		{
			// The actor stopped first. Prefer the hook's own outcome when it completed concurrently
			// with the shutdown; otherwise the queued callback was dropped by the mailbox.
			if (completion.Task.IsCompleted)
			{
				return await completion.Task.ConfigureAwait(false);
			}

			throw new InvalidOperationException(
				$"Actor {handle.Value} stopped before the {operation} hook was dispatched; the operation was abandoned.");
		}

		return await completion.Task.ConfigureAwait(false);
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

		// Mailbox messages are only consumed after the actor's start hook completes (see ActorHost.RunAsync),
		// so enqueueing directly preserves startup ordering. Awaiting host.Startup before enqueueing would
		// deadlock messages an actor sends to itself from within its own start hook.
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

	/// <summary>
	/// Fails the call immediately when <paramref name="to"/> is already suspended somewhere on the
	/// current causal call stack. Kept as a single seam so a future [Reentrant] opt-in can relax
	/// or bypass the check for annotated actors.
	/// </summary>
	/// <param name="to">The target handle of the call about to be issued.</param>
	private void ThrowIfCyclicalCall(ActorHandle to)
	{
		var callChain = ActorCallContext.CurrentChain;
		if (callChain is not null && callChain.Contains(to))
		{
			throw new ActorCallCycleException(
				$"Actor call cycle detected: {callChain.FormatPath(to, ResolveActorName)}");
		}
	}

	internal MessageEnvelope CreateEnvelope(ActorHandle to, ActorHandle from, CallType callType, object payload)
	{
		var messageId = Interlocked.Increment(ref _nextMessageId);
		var traceId = TraceContext.CurrentTraceId ?? TraceContext.EnsureTraceId();
		// Only calls create blocking edges in the call graph, so only calls propagate the chain.
		// A fire-and-forget send breaks the causal chain: the sender's handler is not suspended
		// waiting for the receiver.
		var callChain = callType == CallType.Call ? ActorCallContext.CurrentChain : null;
		return new MessageEnvelope(
			messageId,
			from,
			to,
			callType,
			payload,
			traceId,
			DateTimeOffset.UtcNow,
			TimeToLive: null,
			Serialization.MessageEnvelopeSerializer.WireVersion,
			CallChain: callChain);
	}

	private string? ResolveActorName(ActorHandle handle)
	{
		return _handleToName.TryGetValue(handle.Value, out var name) ? name : null;
	}

	private async ValueTask<bool> RemoveActorAsync(ActorHandle handle, bool notifyRegistry = true)
	{
		if (!_actors.TryRemove(handle.Value, out var host))
		{
			return false;
		}

		if (_handleToName.TryRemove(handle.Value, out string? name))
		{
			_nameToHandle.TryRemove(name, out _);
			if (notifyRegistry && _clusterRegistry is not null)
			{
				_clusterRegistry.UnregisterLocalActor(name, handle);
			}
		}

		Metrics.UnregisterActor(handle);
		// Cancel the actor's pending timers before tearing down its host so due ticks are never dispatched.
		_timers.CancelByActor(handle);
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

		// Stop the timer scheduler after all actors are gone; the background thread is a
		// background thread, so this join cannot keep the process alive.
		_timers.Dispose();

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
