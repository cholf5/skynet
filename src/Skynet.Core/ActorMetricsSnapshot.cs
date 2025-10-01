namespace Skynet.Core;

/// <summary>
/// Represents a snapshot of runtime metrics for a single actor instance.
/// </summary>
public sealed record ActorMetricsSnapshot(
	ActorHandle Handle,
	string? Name,
	Type ImplementationType,
	long QueueLength,
	long ProcessedCount,
	long ExceptionCount,
	TimeSpan AverageProcessingTime,
	DateTimeOffset? LastEnqueuedAt,
	DateTimeOffset? LastProcessedAt,
	DateTimeOffset CreatedAt,
	bool TraceEnabled);
