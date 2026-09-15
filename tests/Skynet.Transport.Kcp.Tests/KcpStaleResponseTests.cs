using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Skynet.Transport.Kcp;
using Xunit;

namespace Skynet.Transport.Kcp.Tests;

/// <summary>
/// KCP-side stale response regression test (mirrors the TCP-side equivalent): a late response whose
/// pending call was already removed by caller cancellation must be dropped, never delivered locally.
/// </summary>
public sealed class KcpStaleResponseTests
{
	[Fact]
	public async Task KcpTransport_ShouldDropStaleResponseWithoutLocalDelivery()
	{
		// The caller cancels while the remote actor is still processing: the pending call is
		// removed by the cancellation registration, and the late response that arrives afterwards
		// has no matching pending call. It must be dropped with a warning instead of being
		// delivered to a local actor as a request.
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				new StaticClusterNodeConfiguration
				{
					NodeId = "node1",
					Host = "127.0.0.1",
					Port = GetFreeUdpPort(),
					HandleOffset = 1000,
					Services = new Dictionary<string, long>(StringComparer.Ordinal)
				},
				new StaticClusterNodeConfiguration
				{
					NodeId = "node2",
					Host = "127.0.0.1",
					Port = GetFreeUdpPort(),
					HandleOffset = 2000,
					Services = new Dictionary<string, long>(StringComparer.Ordinal)
					{
						["delayEcho"] = 2001
					}
				}
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var loggerFactory = new RecordingLoggerFactory();

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new KcpTransport(sys, registry1, CreateKcpTestOptions(), loggerFactory));

		var registry2 = new StaticClusterRegistry(configuration, "node2");
		var loggerFactory2 = new RecordingLoggerFactory();
		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new KcpTransport(sys, registry2, CreateKcpTestOptions(),
				loggerFactory2));
		var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await system2.CreateActorAsync(() => new DelayEchoActor(requestReceived), "delayEcho",
			new ActorCreationOptions { HandleOverride = new ActorHandle(2001) });

		// Cancel only after the request has actually reached the remote actor. A fixed cancellation
		// timer races the dial: on a slow CI machine the handshake can outlive it, the transport then
		// aborts the connect and the request is never sent at all — no late response can exist and
		// the stale-response assertion below fails spuriously.
		using var cts = new CancellationTokenSource();
		var callTask = system1.CallAsync<string>(new ActorHandle(2001), new EchoRequest("late"),
			cancellationToken: cts.Token);
		await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
		cts.Cancel();
		Func<Task> act = () => callTask;
		await act.Should().ThrowAsync<OperationCanceledException>();

		// The remote actor keeps processing; its response arrives ~500ms after the request, after the
		// pending call was already removed by the cancellation above.
		await loggerFactory.WaitForLogAsync("Dropped a stale response", TimeSpan.FromSeconds(10));

		// Regression guard: if the stale response were dispatched to a local actor instead of
		// dropped, the failed delivery would complete a response source, and the resulting fault
		// would be sent back to node2 — which node2 would log as another stale response. (The
		// previous assertion on "Failed to deliver message" was vacuous: the bogus-delivery path
		// has a response source and can never reach that log line.)
		await Task.Delay(300);
		lock (loggerFactory2.LockObject)
		{
			loggerFactory2.Messages.Should().NotContain(
				message => message.Contains("Dropped a stale response", StringComparison.Ordinal),
				"the stale response must be dropped instead of triggering a reverse fault to node2");
		}
	}

	private static KcpTransportOptions CreateKcpTestOptions()
	{
		return new KcpTransportOptions
		{
			HeartbeatInterval = TimeSpan.FromSeconds(60),
			IntervalMilliseconds = 10
		};
	}

	private static int GetFreeUdpPort()
	{
		using var client = new UdpClient(0, AddressFamily.InterNetwork);
		return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
	}

	private sealed class DelayEchoActor(TaskCompletionSource requestReceived) : Actor
	{
		protected override async Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			// Signal first so the caller can cancel while the handler is still running, then delay so
			// the response reliably arrives after the caller canceled.
			requestReceived.TrySetResult();
			await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
			return envelope.Payload is EchoRequest request ? $"delayed:{request.Message}" : null;
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record EchoRequest([property: Key(0)] string Message);
}
