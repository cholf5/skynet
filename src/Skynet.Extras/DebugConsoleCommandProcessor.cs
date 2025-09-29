using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Skynet.Core;

namespace Skynet.Extras;

/// <summary>
/// Provides textual command handling for the debug console server.
/// </summary>
public sealed class DebugConsoleCommandProcessor
{
	private static readonly string[] HelpLines = new[]
	{
		"help                 - Show this help text",
		"list                 - Display all registered actors and key metrics",
		"info <id|name>       - Display detailed metrics for a specific actor",
		"trace <id|name> [on|off] - Toggle or set tracing for an actor",
		"kill <id|name>       - Terminate an actor",
		"exit                 - Close the current console session"
	};

	private readonly IDebugConsoleActorGateway _gateway;

	/// <summary>
	/// Initializes a new instance of the <see cref="DebugConsoleCommandProcessor"/> class.
	/// </summary>
	public DebugConsoleCommandProcessor(IDebugConsoleActorGateway gateway)
	{
		ArgumentNullException.ThrowIfNull(gateway);
		_gateway = gateway;
	}

	/// <summary>
	/// Executes the specified console command.
	/// </summary>
	public Task<DebugConsoleCommandResult> ExecuteAsync(string? commandText, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(commandText))
		{
			return Task.FromResult(new DebugConsoleCommandResult(string.Empty, false));
		}

		var tokens = commandText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var command = tokens[0].ToLowerInvariant();
		switch (command)
		{
			case "help":
			return Task.FromResult(new DebugConsoleCommandResult(string.Join(Environment.NewLine, HelpLines), false));
			case "list":
			case "metrics":
			return Task.FromResult(new DebugConsoleCommandResult(RenderActorList(), false));
			case "info":
			return Task.FromResult(RenderInfo(tokens));
			case "trace":
			return Task.FromResult(RenderTrace(tokens));
			case "kill":
			return ExecuteKillAsync(tokens, cancellationToken);
			case "exit":
			case "quit":
			return Task.FromResult(new DebugConsoleCommandResult("Closing session.", true));
			default:
			return Task.FromResult(new DebugConsoleCommandResult($"Unknown command '{command}'. Type 'help' for assistance.", false));
		}
	}

	private DebugConsoleCommandResult RenderInfo(string[] tokens)
	{
		if (tokens.Length < 2)
		{
			return new DebugConsoleCommandResult("Usage: info <id|name>", false);
		}

		if (!_gateway.TryResolveHandle(tokens[1], out var handle, out var error))
		{
			return new DebugConsoleCommandResult(error ?? "Actor not found.", false);
		}

		if (!_gateway.Metrics.TryGetSnapshot(handle, out var snapshot))
		{
			return new DebugConsoleCommandResult($"Metrics unavailable for actor {handle.Value}.", false);
		}

		var builder = new StringBuilder();
		builder.AppendLine($"Handle: {snapshot.Handle.Value}");
		builder.AppendLine($"Name: {snapshot.Name ?? "<unnamed>"}");
		builder.AppendLine($"Type: {snapshot.ImplementationType.FullName}");
		builder.AppendLine($"Queue Length: {snapshot.QueueLength}");
		builder.AppendLine($"Processed: {snapshot.ProcessedCount}");
		builder.AppendLine($"Exceptions: {snapshot.ExceptionCount}");
		builder.AppendLine($"Average Processing: {snapshot.AverageProcessingTime.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms");
		builder.AppendLine($"Last Enqueued: {FormatTimestamp(snapshot.LastEnqueuedAt)}");
		builder.AppendLine($"Last Processed: {FormatTimestamp(snapshot.LastProcessedAt)}");
		builder.AppendLine($"Created: {snapshot.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}");
		builder.AppendLine($"Tracing: {(snapshot.TraceEnabled ? "on" : "off")}");
		return new DebugConsoleCommandResult(builder.ToString(), false);
	}

	private DebugConsoleCommandResult RenderTrace(string[] tokens)
	{
		if (tokens.Length < 2)
		{
			return new DebugConsoleCommandResult("Usage: trace <id|name> [on|off]", false);
		}

		if (!_gateway.TryResolveHandle(tokens[1], out var handle, out var error))
		{
			return new DebugConsoleCommandResult(error ?? "Actor not found.", false);
		}

		var metrics = _gateway.Metrics;
		var enable = tokens.Length < 3
		? !metrics.IsTracing(handle)
		: tokens[2].Equals("on", StringComparison.OrdinalIgnoreCase) || tokens[2].Equals("true", StringComparison.OrdinalIgnoreCase);
		var changed = enable ? metrics.EnableTrace(handle) : metrics.DisableTrace(handle);
		var state = enable ? "enabled" : "disabled";
		var message = changed ? $"Tracing {state} for actor {handle.Value}." : $"Tracing already {state} for actor {handle.Value}.";
		return new DebugConsoleCommandResult(message, false);
	}

	private async Task<DebugConsoleCommandResult> ExecuteKillAsync(string[] tokens, CancellationToken cancellationToken)
	{
		if (tokens.Length < 2)
		{
			return new DebugConsoleCommandResult("Usage: kill <id|name>", false);
		}

		if (!_gateway.TryResolveHandle(tokens[1], out var handle, out var error))
		{
			return new DebugConsoleCommandResult(error ?? "Actor not found.", false);
		}

		var killed = await _gateway.KillAsync(handle, cancellationToken).ConfigureAwait(false);
		return new DebugConsoleCommandResult(killed ? $"Actor {handle.Value} terminated." : $"Actor {handle.Value} could not be terminated.", false);
	}

	private string RenderActorList()
	{
		var snapshots = _gateway.Metrics.GetSnapshot()
		.OrderBy(s => s.Handle.Value)
		.ToArray();
		if (snapshots.Length == 0)
		{
			return "No actors registered.";
		}

		var builder = new StringBuilder();
		builder.AppendLine("Handle   Name                Queue Processed Errors Avg(ms) Trace");
		foreach (var snapshot in snapshots)
		{
			builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
			"{0,6}   {1,-18} {2,5} {3,9} {4,6} {5,7:F2} {6}",
			snapshot.Handle.Value,
			Truncate(snapshot.Name ?? "<unnamed>", 18),
			snapshot.QueueLength,
			snapshot.ProcessedCount,
			snapshot.ExceptionCount,
			snapshot.AverageProcessingTime.TotalMilliseconds,
			snapshot.TraceEnabled ? "on" : "off"));
		}
		return builder.ToString();
	}

	private static string Truncate(string value, int max)
	{
		return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
	}

	private static string FormatTimestamp(DateTimeOffset? timestamp)
	{
		return timestamp.HasValue ? timestamp.Value.ToString("O", CultureInfo.InvariantCulture) : "<never>";
	}
}
