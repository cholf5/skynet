using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Skynet.Core;
using Xunit;

namespace Skynet.Extras.Tests;

public sealed class PrometheusMetricsExporterTests
{
	private sealed class TestActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return Task.FromResult<object?>(null);
		}
	}

	[Fact]
	public void RenderMetrics_ShouldEmitSeriesForRegisteredActor()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(7);
		collector.RegisterActor(handle, "login", typeof(TestActor));
		collector.OnMessageEnqueued(handle);
		collector.OnMessageDequeued(handle);
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(2), success: true);
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(4), success: false);
		collector.OnSendEnqueueFailed();

		var exporter = new PrometheusMetricsExporter(collector, new PrometheusMetricsExporterOptions { InstanceLabel = "node-1" });
		var text = exporter.RenderMetrics();

		text.Should().Contain("# TYPE skynet_actor_queue_length gauge");
		text.Should().Contain($"skynet_actor_queue_length{{instance=\"node-1\",handle=\"7\",name=\"login\",type=\"TestActor\"}} 0");
		text.Should().Contain($"skynet_actor_messages_processed_total{{instance=\"node-1\",handle=\"7\",name=\"login\",type=\"TestActor\"}} 2");
		text.Should().Contain($"skynet_actor_exceptions_total{{instance=\"node-1\",handle=\"7\",name=\"login\",type=\"TestActor\"}} 1");
		text.Should().Contain("# TYPE skynet_actor_messages_processed_total counter");
		text.Should().Contain("# TYPE skynet_actor_exceptions_total counter");
		// Average of 2 ms and 4 ms.
		text.Should().Contain($"skynet_actor_avg_processing_milliseconds{{instance=\"node-1\",handle=\"7\",name=\"login\",type=\"TestActor\"}} 3");
		text.Should().Contain("skynet_actor_uptime_seconds{");
		text.Should().Contain("skynet_actor_last_enqueue_timestamp_seconds{");
		text.Should().Contain("skynet_actor_last_processed_timestamp_seconds{");
		text.Should().Contain("skynet_send_enqueue_failures_total{instance=\"node-1\"} 1");
		text.Should().Contain("# TYPE skynet_send_enqueue_failures_total counter");
		text.Should().Contain("skynet_process_managed_memory_bytes{instance=\"node-1\"} ");
		text.Should().Contain("skynet_process_uptime_seconds{instance=\"node-1\"} ");
	}

	[Fact]
	public void RenderMetrics_ShouldEscapeLabelValues()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(1);
		var weirdName = "weird \"name\" with\\slash\nnewline";
		collector.RegisterActor(handle, weirdName, typeof(TestActor));

		var exporter = new PrometheusMetricsExporter(collector);
		var text = exporter.RenderMetrics();

		text.Should().Contain("name=\"weird \\\"name\\\" with\\\\slash\\nnewline\"");
		text.Should().NotContain(weirdName);
	}

	[Fact]
	public void RenderMetrics_WithoutInstanceLabel_ShouldOmitLabelBracesOnSystemSeries()
	{
		var collector = new ActorMetricsCollector();
		var exporter = new PrometheusMetricsExporter(collector);
		var text = exporter.RenderMetrics();

		text.Should().Contain("\nskynet_send_enqueue_failures_total 0\n");
		text.Should().Contain("\nskynet_process_managed_memory_bytes ");
		text.Should().NotContain("{}");
	}

	[Fact]
	public void Options_ShouldRejectInvalidValues()
	{
		var create = (string host, int port) => new PrometheusMetricsExporterOptions { Host = host, Port = port };
		var act1 = () => new PrometheusMetricsExporter(new ActorMetricsCollector(), create("", 9092));
		var act2 = () => new PrometheusMetricsExporter(new ActorMetricsCollector(), create("localhost", 0));
		var act3 = () => new PrometheusMetricsExporter(new ActorMetricsCollector(), create("localhost", 70000));

		act1.Should().Throw<InvalidOperationException>();
		act2.Should().Throw<InvalidOperationException>();
		act3.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public async Task HttpEndpoint_ShouldServeMetrics()
	{
		var collector = new ActorMetricsCollector();
		var handle = new ActorHandle(3);
		collector.RegisterActor(handle, "echo", typeof(TestActor));
		collector.OnMessageProcessed(handle, TimeSpan.FromMilliseconds(1), success: true);

		var port = GetFreeTcpPort();
		await using var exporter = new PrometheusMetricsExporter(collector,
			new PrometheusMetricsExporterOptions { Port = port });
		await exporter.StartAsync().ConfigureAwait(false);
		exporter.MetricsUri.Should().NotBeNull();

		using var client = new HttpClient();
		var response = await client.GetAsync(exporter.MetricsUri!).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		response.StatusCode.Should().Be(HttpStatusCode.OK);
		response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

		var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		body.Should().Contain("skynet_actor_queue_length{handle=\"3\",name=\"echo\",type=\"TestActor\"} 0");

		await exporter.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task HttpEndpoint_ShouldReturn404ForUnknownPathAnd405ForPost()
	{
		var collector = new ActorMetricsCollector();
		var port = GetFreeTcpPort();
		await using var exporter = new PrometheusMetricsExporter(collector,
			new PrometheusMetricsExporterOptions { Port = port, MetricsPath = "/skynet-metrics/" });
		await exporter.StartAsync().ConfigureAwait(false);
		var baseUri = new Uri($"http://localhost:{port}");
		using var client = new HttpClient();

		var miss = await client.GetAsync(new Uri(baseUri, "/other/")).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		miss.StatusCode.Should().Be(HttpStatusCode.NotFound);

		using var post = new HttpRequestMessage(HttpMethod.Post, exporter.MetricsUri)
		{
			Content = new StringContent("", Encoding.UTF8, "text/plain")
		};
		var rejected = await client.SendAsync(post).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		rejected.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

		await exporter.StopAsync().ConfigureAwait(false);
	}

	[Fact]
	public async Task StartTwice_ShouldThrow()
	{
		var port = GetFreeTcpPort();
		await using var exporter = new PrometheusMetricsExporter(new ActorMetricsCollector(),
			new PrometheusMetricsExporterOptions { Port = port });
		await exporter.StartAsync().ConfigureAwait(false);

		var act = async () => await exporter.StartAsync().ConfigureAwait(false);
		await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);

		await exporter.StopAsync().ConfigureAwait(false);
	}

	private static int GetFreeTcpPort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}
}
