using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;

namespace Skynet.Benchmarks;

/// <summary>
/// Lightweight benchmark harness for Skynet baseline scenarios (PRD Milestone 7). Deliberately not
/// BenchmarkDotNet: the harness runs fixed-duration closed-loop measurements that stay fast enough
/// for CI and produce one stable, scriptable number set per scenario.
///
/// Scenarios:
///   local-tell   — fire-and-forget sends to a single no-op actor (throughput, backpressured).
///   local-call   — sequential or concurrent CallAsync roundtrips against a no-op actor (latency).
///   remote-call  — CallAsync roundtrips across two in-process nodes connected via TcpTransport.
/// </summary>
internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
		{
			PrintUsage();
			return 0;
		}

		string? scenario = null;
		var duration = TimeSpan.FromSeconds(5);
		var concurrency = 1;
		string? jsonPath = null;
		for (var i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--scenario" when i + 1 < args.Length:
					scenario = args[++i];
					break;
				case "--duration" when i + 1 < args.Length && double.TryParse(args[++i], CultureInfo.InvariantCulture, out var seconds):
					duration = TimeSpan.FromSeconds(Math.Max(1, seconds));
					break;
				case "--concurrency" when i + 1 < args.Length && int.TryParse(args[++i], out var c):
					concurrency = Math.Max(1, c);
					break;
				case "--json" when i + 1 < args.Length:
					jsonPath = args[++i];
					break;
				default:
					Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
					PrintUsage();
					return 2;
			}
		}

		BenchmarkResult result;
		switch (scenario)
		{
			case "local-tell":
				result = await RunLocalTellAsync(duration, concurrency).ConfigureAwait(false);
				break;
			case "local-call":
				result = await RunLocalCallAsync(duration, concurrency).ConfigureAwait(false);
				break;
			case "remote-call":
				result = await RunRemoteCallAsync(duration, concurrency).ConfigureAwait(false);
				break;
			default:
				Console.Error.WriteLine($"Unknown scenario '{scenario}'. Available: local-tell, local-call, remote-call.");
				return 2;
		}

		PrintResult(result);
		if (jsonPath is not null)
		{
			var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
			await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(false);
			Console.WriteLine($"JSON results written to {jsonPath}");
		}

		return 0;
	}

	private static void PrintUsage()
	{
		Console.WriteLine("Usage: dotnet run -c Release -- --scenario <name> [--duration seconds] [--concurrency n] [--json path]");
		Console.WriteLine();
		Console.WriteLine("Scenarios:");
		Console.WriteLine("  local-tell    Fire-and-forget sends to one no-op actor (throughput).");
		Console.WriteLine("  local-call    CallAsync roundtrips against a local no-op actor (latency).");
		Console.WriteLine("  remote-call   CallAsync roundtrips over TcpTransport between two in-process nodes.");
	}

	private static async Task<BenchmarkResult> RunLocalTellAsync(TimeSpan duration, int concurrency)
	{
		await using var system = new ActorSystem();
		var handle = (await system.CreateActorAsync(() => new NoopActor()).ConfigureAwait(false)).Handle;

		await WarmupAsync(() => system.SendAsync(handle, new Ping("warmup")).AsTask()).ConfigureAwait(false);
		var before = GetProcessed(system, handle);

		using var cts = new CancellationTokenSource(duration);
		var sent = 0L;
		var stopwatch = Stopwatch.StartNew();
		var producers = Enumerable.Range(0, concurrency)
			.Select(_ => Task.Run(() => RunTellProducerAsync(system, handle, cts.Token, () => Interlocked.Increment(ref sent))))
			.ToArray();
		await Task.WhenAll(producers).ConfigureAwait(false);
		stopwatch.Stop();

		// Drain: wait until every sent message has been processed (bounded to avoid hangs).
		await DrainAsync(system, handle, Interlocked.Read(ref sent), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		var after = GetProcessed(system, handle);

		return new BenchmarkResult
		{
			Scenario = "local-tell",
			DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
			Concurrency = concurrency,
			TotalOps = after - before,
			OpsPerSecond = (after - before) / stopwatch.Elapsed.TotalSeconds,
		};
	}

	private static async Task RunTellProducerAsync(ActorSystem system, ActorHandle handle, CancellationToken token, Action onSent)
	{
		while (!token.IsCancellationRequested)
		{
			await system.SendAsync(handle, new Ping("x")).ConfigureAwait(false);
			onSent();

			// Closed-loop backpressure: keep the mailbox bounded so we measure sustained
			// throughput instead of how fast the queue can grow.
			if (system.Metrics.TryGetSnapshot(handle, out var snapshot) && snapshot.QueueLength > 100_000)
			{
				await Task.Yield();
			}
		}
	}

	private static async Task<BenchmarkResult> RunLocalCallAsync(TimeSpan duration, int concurrency)
	{
		await using var system = new ActorSystem();
		var handle = (await system.CreateActorAsync(() => new PingActor()).ConfigureAwait(false)).Handle;

		var samples = new ConcurrentBag<long>();
		await WarmupAsync(() => system.CallAsync<string>(handle, new Ping("warmup"))).ConfigureAwait(false);

		using var cts = new CancellationTokenSource(duration);
		var stopwatch = Stopwatch.StartNew();
		var callers = Enumerable.Range(0, concurrency)
			.Select(_ => Task.Run(() => RunCallLoopAsync(system, handle, cts.Token, samples)))
			.ToArray();
		await Task.WhenAll(callers).ConfigureAwait(false);
		stopwatch.Stop();

		return BuildLatencyResult("local-call", stopwatch.Elapsed, concurrency, samples);
	}

	private static async Task RunCallLoopAsync(ActorSystem system, ActorHandle handle, CancellationToken token,
		ConcurrentBag<long> samples)
	{
		while (!token.IsCancellationRequested)
		{
			var start = Stopwatch.GetTimestamp();
			try
			{
				await system.CallAsync<string>(handle, new Ping("x")).WaitAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break; // Measurement window closed mid-call; drop the unfinished sample.
			}

			samples.Add(Stopwatch.GetTimestamp() - start);
		}
	}

	private static async Task<BenchmarkResult> RunRemoteCallAsync(TimeSpan duration, int concurrency)
	{
		var callerRegistry = new StaticClusterRegistry(BuildClusterConfiguration(), "bench-a");
		var hostRegistry = new StaticClusterRegistry(BuildClusterConfiguration(), "bench-b");
		var transportOptions = new TcpTransportOptions { HeartbeatInterval = TimeSpan.FromSeconds(5) };

		await using var host = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = hostRegistry },
			transportFactory: sys => new TcpTransport(sys, hostRegistry, transportOptions, NullLoggerFactory.Instance));
		await host.CreateActorAsync(() => new PingActor(), "noop", new ActorCreationOptions
		{
			HandleOverride = new ActorHandle(2001)
		}).ConfigureAwait(false);

		await using var caller = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = callerRegistry },
			transportFactory: sys => new TcpTransport(sys, callerRegistry, transportOptions, NullLoggerFactory.Instance));
		var remote = caller.GetByName("noop");

		var samples = new ConcurrentBag<long>();
		await WarmupAsync(() => remote.CallAsync<string>(new Ping("warmup"), TimeSpan.FromSeconds(5))).ConfigureAwait(false);

		using var cts = new CancellationTokenSource(duration);
		var stopwatch = Stopwatch.StartNew();
		var callers = Enumerable.Range(0, concurrency)
			.Select(_ => Task.Run(() => RunRemoteCallLoopAsync(remote, cts.Token, samples)))
			.ToArray();
		await Task.WhenAll(callers).ConfigureAwait(false);
		stopwatch.Stop();

		return BuildLatencyResult("remote-call", stopwatch.Elapsed, concurrency, samples);
	}

	private static StaticClusterConfiguration BuildClusterConfiguration()
	{
		return new StaticClusterConfiguration
		{
			Nodes =
			[
				new StaticClusterNodeConfiguration { NodeId = "bench-a", Host = "127.0.0.1", Port = 9221, HandleOffset = 1000 },
				new StaticClusterNodeConfiguration
				{
					NodeId = "bench-b",
					Host = "127.0.0.1",
					Port = 9222,
					HandleOffset = 2000,
					Services = new Dictionary<string, long>(StringComparer.Ordinal) { ["noop"] = 2001 }
				}
			]
		};
	}

	private static async Task RunRemoteCallLoopAsync(ActorRef remote, CancellationToken token, ConcurrentBag<long> samples)
	{
		while (!token.IsCancellationRequested)
		{
			var start = Stopwatch.GetTimestamp();
			try
			{
				await remote.CallAsync<string>(new Ping("x"), TimeSpan.FromSeconds(5)).WaitAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				break; // Measurement window closed mid-call; drop the unfinished sample.
			}

			samples.Add(Stopwatch.GetTimestamp() - start);
		}
	}

	private static async Task WarmupAsync(Func<Task> operation)
	{
		for (var i = 0; i < 1_000; i++)
		{
			await operation().ConfigureAwait(false);
		}

		await Task.Delay(100).ConfigureAwait(false);
	}

	private static long GetProcessed(ActorSystem system, ActorHandle handle)
	{
		if (!system.Metrics.TryGetSnapshot(handle, out var snapshot))
		{
			throw new InvalidOperationException("Benchmark actor has no metrics snapshot.");
		}

		return snapshot.ProcessedCount;
	}

	private static async Task DrainAsync(ActorSystem system, ActorHandle handle, long expected, TimeSpan timeout)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (GetProcessed(system, handle) >= expected
				&& system.Metrics.TryGetSnapshot(handle, out var snapshot)
				&& snapshot.QueueLength == 0)
			{
				return;
			}

			await Task.Delay(10).ConfigureAwait(false);
		}
	}

	private static BenchmarkResult BuildLatencyResult(string scenario, TimeSpan elapsed, int concurrency, ConcurrentBag<long> samples)
	{
		var ticks = samples.ToArray();
		Array.Sort(ticks);
		double Percentile(double p)
		{
			if (ticks.Length == 0)
			{
				return 0;
			}

			var index = (int)Math.Ceiling(p * ticks.Length) - 1;
			return TimeSpan.FromTicks(ticks[Math.Clamp(index, 0, ticks.Length - 1)]).TotalMilliseconds;
		}

		return new BenchmarkResult
		{
			Scenario = scenario,
			DurationSeconds = Math.Round(elapsed.TotalSeconds, 3),
			Concurrency = concurrency,
			TotalOps = ticks.LongLength,
			OpsPerSecond = ticks.Length / elapsed.TotalSeconds,
			Latency = new LatencyStats
			{
				MinMs = Math.Round(Percentile(0), 3),
				P50Ms = Math.Round(Percentile(0.50), 3),
				P90Ms = Math.Round(Percentile(0.90), 3),
				P99Ms = Math.Round(Percentile(0.99), 3),
				MaxMs = Math.Round(Percentile(1), 3),
			},
		};
	}

	private static void PrintResult(BenchmarkResult result)
	{
		var builder = new StringBuilder();
		builder.AppendLine($"scenario:     {result.Scenario}");
		builder.AppendLine($"duration:     {result.DurationSeconds}s");
		builder.AppendLine($"concurrency:  {result.Concurrency}");
		builder.AppendLine($"total ops:    {result.TotalOps}");
		builder.AppendLine($"ops/sec:      {result.OpsPerSecond.ToString("N0", CultureInfo.InvariantCulture)}");
		if (result.Latency is { } latency)
		{
			builder.AppendLine($"latency min:  {latency.MinMs} ms");
			builder.AppendLine($"latency p50:  {latency.P50Ms} ms");
			builder.AppendLine($"latency p90:  {latency.P90Ms} ms");
			builder.AppendLine($"latency p99:  {latency.P99Ms} ms");
			builder.AppendLine($"latency max:  {latency.MaxMs} ms");
		}

		Console.WriteLine(builder.ToString());
	}

	[MessagePack.MessagePackObject(AllowPrivate = true)]
	internal sealed record Ping([property: MessagePack.Key(0)] string Text);

	private sealed record BenchmarkResult
	{
		public string Scenario { get; init; } = string.Empty;

		public double DurationSeconds { get; init; }

		public int Concurrency { get; init; }

		public long TotalOps { get; init; }

		public double OpsPerSecond { get; init; }

		public LatencyStats? Latency { get; init; }
	}

	private sealed record LatencyStats
	{
		public double MinMs { get; init; }

		public double P50Ms { get; init; }

		public double P90Ms { get; init; }

		public double P99Ms { get; init; }

		public double MaxMs { get; init; }
	}

	/// <summary>Consumes fire-and-forget messages without any work.</summary>
	private sealed class NoopActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return Task.FromResult<object?>(null);
		}
	}

	/// <summary>Answers CallAsync with a tiny constant string.</summary>
	private sealed class PingActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return envelope.Payload is Ping ? Task.FromResult<object?>("ok") : Task.FromResult<object?>(null);
		}
	}
}
