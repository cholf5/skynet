using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Core;

namespace Skynet.Extras;

/// <summary>
/// Exposes <see cref="ActorMetricsCollector"/> snapshots in the Prometheus text exposition format
/// (version 0.0.4) over a built-in <see cref="System.Net.HttpListener"/> endpoint. Snapshots are
/// pulled at scrape time, so exporting adds no cost when nobody scrapes. The implementation carries
/// no third-party dependencies; scrapes are served sequentially, which is ample for Prometheus'
/// default 15-60 second scrape intervals.
/// </summary>
public sealed class PrometheusMetricsExporter : IAsyncDisposable
{
	private readonly ActorMetricsCollector _collector;
	private readonly PrometheusMetricsExporterOptions _options;
	private readonly ILogger<PrometheusMetricsExporter> _logger;
	private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
	private HttpListener? _listener;
	private CancellationTokenSource? _lifetimeCts;
	private Task? _listenLoop;
	private bool _disposed;

	public PrometheusMetricsExporter(ActorMetricsCollector collector, PrometheusMetricsExporterOptions? options = null,
		ILogger<PrometheusMetricsExporter>? logger = null)
	{
		_collector = collector ?? throw new ArgumentNullException(nameof(collector));
		_options = options ?? new PrometheusMetricsExporterOptions();
		_options.Validate();
		_logger = logger ?? NullLogger<PrometheusMetricsExporter>.Instance;
	}

	/// <summary>
	/// Gets the URI Prometheus instances should scrape.
	/// </summary>
	public Uri? MetricsUri
	{
		get;
		private set;
	}

	/// <summary>
	/// Starts the metrics HTTP endpoint.
	/// </summary>
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_lifetimeCts is not null)
		{
			throw new InvalidOperationException("Prometheus exporter already started.");
		}

		_lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var path = _options.MetricsPath.EndsWith('/') ? _options.MetricsPath : _options.MetricsPath + "/";
		_listener = new HttpListener();
		_listener.Prefixes.Add($"http://{_options.Host}:{_options.Port}{path}");
		_listener.Start();
		MetricsUri = new Uri($"http://{FormatHostForUri(_options.Host)}:{_options.Port}{path}");
		_listenLoop = Task.Run(() => ListenAsync(_lifetimeCts.Token), CancellationToken.None);
		_logger.LogInformation("Prometheus exporter serving metrics on {MetricsUri}.", MetricsUri);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Stops the metrics HTTP endpoint.
	/// </summary>
	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		var cts = _lifetimeCts;
		if (cts is null)
		{
			return;
		}

		await cts.CancelAsync().ConfigureAwait(false);
		_listener?.Stop();
		_listener?.Close();
		_listener = null;

		if (_listenLoop is not null)
		{
			try
			{
				await _listenLoop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Prometheus exporter listen loop ended with an error.");
			}
		}

		_listenLoop = null;
		cts.Dispose();
		_lifetimeCts = null;
		MetricsUri = null;
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

	/// <summary>
	/// Renders the current metrics snapshot in the Prometheus text exposition format. Exposed publicly
	/// so it can be embedded into an application-owned HTTP endpoint (e.g. ASP.NET Core) instead of
	/// the built-in listener.
	/// </summary>
	public string RenderMetrics()
	{
		var writer = new StringBuilder();
		var instance = _options.InstanceLabel is null
			? string.Empty
			: $"instance=\"{Escape(_options.InstanceLabel)}\",";

		foreach (var snapshot in _collector.GetSnapshot())
		{
			var labels = $"{instance}handle=\"{snapshot.Handle.Value}\",name=\"{Escape(snapshot.Name ?? string.Empty)}\",type=\"{Escape(snapshot.ImplementationType.Name)}\"";
			AppendGauge(writer, "skynet_actor_queue_length", "Current mailbox queue length.", labels,
				snapshot.QueueLength.ToString(CultureInfo.InvariantCulture));
			AppendCounter(writer, "skynet_actor_messages_processed_total", "Messages processed by the actor.", labels,
				snapshot.ProcessedCount);
			AppendCounter(writer, "skynet_actor_exceptions_total", "Message processing exceptions raised by the actor.", labels,
				snapshot.ExceptionCount);
			AppendGauge(writer, "skynet_actor_avg_processing_milliseconds", "Average message processing time in milliseconds.", labels,
				snapshot.AverageProcessingTime.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture));
			AppendGauge(writer, "skynet_actor_uptime_seconds", "Seconds since the actor was registered.", labels,
				(DateTimeOffset.UtcNow - snapshot.CreatedAt).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

			if (snapshot.LastEnqueuedAt is { } enqueued)
			{
				AppendGauge(writer, "skynet_actor_last_enqueue_timestamp_seconds", "Unix timestamp of the last mailbox enqueue.", labels,
					enqueued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
			}

			if (snapshot.LastProcessedAt is { } processed)
			{
				AppendGauge(writer, "skynet_actor_last_processed_timestamp_seconds", "Unix timestamp of the last processed message.", labels,
					processed.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
			}
		}

		var systemLabels = instance.TrimEnd(',');
		AppendCounter(writer, "skynet_send_enqueue_failures_total", "Fire-and-forget send enqueues that failed system-wide.", systemLabels,
			_collector.SendEnqueueFailureCount);
		AppendGauge(writer, "skynet_process_managed_memory_bytes", "Managed memory bytes reported by the GC.", systemLabels,
			GC.GetTotalMemory(forceFullCollection: false).ToString(CultureInfo.InvariantCulture));
		AppendGauge(writer, "skynet_process_uptime_seconds", "Seconds since the exporter was created.", systemLabels,
			(DateTimeOffset.UtcNow - _startedAt).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

		return writer.ToString();
	}

	private async Task ListenAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener!.GetContextAsync().ConfigureAwait(false);
			}
			catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				_logger.LogWarning(ex, "Prometheus exporter failed to accept a scrape request.");
				continue;
			}

			await ServeAsync(context, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
	{
		try
		{
			var request = context.Request;
			var response = context.Response;
			var prefix = _options.MetricsPath.EndsWith('/') ? _options.MetricsPath : _options.MetricsPath + "/";
			if (!request.Url!.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
			{
				response.StatusCode = (int)HttpStatusCode.NotFound;
				response.Close();
				return;
			}

			if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
			{
				response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
				response.Close();
				return;
			}

			var body = Encoding.UTF8.GetBytes(RenderMetrics());
			response.StatusCode = (int)HttpStatusCode.OK;
			response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
			response.ContentLength64 = body.Length;
			await response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
			response.Close();
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to serve a Prometheus scrape request.");
			try
			{
				context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				context.Response.Close();
			}
			catch
			{
				// The connection is already gone; nothing to report to.
			}
		}
	}

	private static void AppendCounter(StringBuilder writer, string name, string help, string labels, long value)
	{
		writer.Append("# HELP ").Append(name).Append(' ').Append(help)
			.Append("\n# TYPE ").Append(name).Append(" counter\n")
			.Append(Series(name, labels)).Append(' ')
			.Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
	}

	private static void AppendGauge(StringBuilder writer, string name, string help, string labels, string value)
	{
		writer.Append("# HELP ").Append(name).Append(' ').Append(help)
			.Append("\n# TYPE ").Append(name).Append(" gauge\n")
			.Append(Series(name, labels)).Append(' ')
			.Append(value).Append('\n');
	}

	private static string Series(string name, string labels)
	{
		return labels.Length == 0 ? name : $"{name}{{{labels}}}";
	}

	private static string Escape(string value)
	{
		return value.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal);
	}

	private static string FormatHostForUri(string host)
	{
		return host is "+" or "*" ? "localhost" : host;
	}
}
