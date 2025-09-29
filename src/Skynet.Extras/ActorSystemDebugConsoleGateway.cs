using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Skynet.Core;

namespace Skynet.Extras;

/// <summary>
/// Provides a bridge between an <see cref="ActorSystem"/> and the debug console infrastructure.
/// </summary>
public sealed class ActorSystemDebugConsoleGateway : IDebugConsoleActorGateway
{
	private readonly ActorSystem _system;

	/// <summary>
	/// Initializes a new instance of the <see cref="ActorSystemDebugConsoleGateway"/> class.
	/// </summary>
	/// <param name="system">The actor system to expose to the console.</param>
	public ActorSystemDebugConsoleGateway(ActorSystem system)
	{
		ArgumentNullException.ThrowIfNull(system);
		_system = system;
	}

	/// <inheritdoc />
	public ActorMetricsCollector Metrics => _system.Metrics;

	/// <inheritdoc />
	public IReadOnlyCollection<ActorDescriptor> ListActors()
	{
		return _system.ListActors();
	}

	/// <inheritdoc />
	public bool TryResolveHandle(string identifier, out ActorHandle handle, out string? error)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
				handle = ActorHandle.None;
				error = "Identifier cannot be empty.";
				return false;
		}

		if (long.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
		{
				handle = new ActorHandle(numeric);
		}
		else if (_system.TryGetHandleByName(identifier, out var named))
		{
				handle = named;
		}
		else
		{
				handle = ActorHandle.None;
				error = $"Unknown actor '{identifier}'.";
				return false;
		}

		if (!handle.IsValid)
		{
				error = $"Handle {handle.Value} is invalid.";
				handle = ActorHandle.None;
				return false;
		}

		if (!Metrics.TryGetSnapshot(handle, out _))
		{
				error = $"Actor {handle.Value} is not registered.";
				handle = ActorHandle.None;
				return false;
		}

		error = null;
		return true;
	}

	/// <inheritdoc />
	public Task<bool> KillAsync(ActorHandle handle, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return _system.KillAsync(handle);
	}
}
