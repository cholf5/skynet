namespace Skynet.Extras;

/// <summary>
/// Configuration for <see cref="PrometheusMetricsExporter"/>.
/// </summary>
public sealed class PrometheusMetricsExporterOptions
{
	private string _metricsPath = "/metrics/";

	/// <summary>
	/// Gets or sets the HTTP host the metrics endpoint binds to. Use <c>+</c> or <c>*</c> to bind all
	/// interfaces; the default binds to localhost only.
	/// </summary>
	public string Host { get; set; } = "localhost";

	/// <summary>
	/// Gets or sets the HTTP port the metrics endpoint binds to.
	/// </summary>
	public int Port { get; set; } = 9092;

	/// <summary>
	/// Gets or sets the request path of the metrics endpoint. Always normalized to end with a slash
	/// because <see cref="System.Net.HttpListener"/> requires it.
	/// </summary>
	public string MetricsPath
	{
		get => _metricsPath;
		set => _metricsPath = string.IsNullOrWhiteSpace(value) ? "/metrics/" : value;
	}

	/// <summary>
	/// Gets or sets an optional <c>instance</c> label attached to every exported series, useful when
	/// several Skynet nodes scrape into one Prometheus. When null no instance label is emitted.
	/// </summary>
	public string? InstanceLabel { get; set; }

	/// <summary>
	/// Validates the configuration and throws if invalid.
	/// </summary>
	public void Validate()
	{
		if (Port < 1 || Port > 65535)
		{
			throw new InvalidOperationException("Port must be between 1 and 65535.");
		}

		if (string.IsNullOrWhiteSpace(Host))
		{
			throw new InvalidOperationException("Host must be specified.");
		}
	}
}
