using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Skynet.Cluster;
using Skynet.Core;
using Xunit;

namespace Skynet.Core.Tests;

/// <summary>
/// Optional TLS (<see cref="SslStream"/>) coverage for <see cref="TcpTransport"/>: encrypted
/// roundtrips, fail-fast on TLS/plaintext configuration mismatches, and client-side certificate
/// rejection. Plaintext behavior without any TLS option is covered by <see cref="TcpTransportTests"/>.
/// </summary>
public sealed class TcpTransportTlsTests
{
	[Fact]
	public async Task CallAsync_ShouldRoundtripOverTlsWhenBothEndsUseTls()
	{
		using var certificate = CreateSelfSignedCertificate();
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		// node1 serves inbound TLS (it is the TLS server); node2 dials with TLS enabled (TLS client).
		// The callback records what the handshake actually negotiated before trusting the self-signed
		// certificate, proving the link really is TLS.
		SslPolicyErrors validationErrors = SslPolicyErrors.None;
		string? presentedThumbprint = null;

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250),
				TlsServerCertificate = certificate
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new TlsEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromMilliseconds(250),
				UseTls = true,
				RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
				{
					validationErrors = errors;
					presentedThumbprint = cert?.GetCertHashString();
					return true;
				}
			}, NullLoggerFactory.Instance));

		var remote = system2.GetByName("echo");
		var result = await remote.CallAsync<string>(new TlsEchoRequest("ping"), TimeSpan.FromSeconds(5));
		result.Should().Be("echo:ping");

		// The TLS handshake really ran against the configured certificate: the self-signed chain
		// fails platform validation (hence the callback), and the presented certificate is ours.
		validationErrors.Should().NotBe(SslPolicyErrors.None,
			"the self-signed test certificate must fail default chain validation before the callback trusts it");
		presentedThumbprint.Should().Be(certificate.GetCertHashString(),
			"the server must present the configured TLS certificate");
	}

	[Theory]
	[InlineData(true, "TLS client against a plaintext server")]
	[InlineData(false, "plaintext client against a TLS server")]
	public async Task CallAsync_ShouldFailFastWhenOnlyOneEndUsesTls(bool clientUsesTls, string scenario)
	{
		using var certificate = CreateSelfSignedCertificate();
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");

		// Only one direction of the link is TLS-enabled; the other stays plaintext.
		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60),
				TlsServerCertificate = clientUsesTls ? null : certificate
			}, NullLoggerFactory.Instance));
		await system1.CreateActorAsync(() => new TlsEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		TcpTransport? transport2 = null;
		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => transport2 = new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60),
				UseTls = clientUsesTls,
				RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
			}, NullLoggerFactory.Instance));

		var call = system2.GetByName("echo").CallAsync<string>(new TlsEchoRequest("ping"),
			TimeSpan.FromSeconds(30));
		Func<Task> act = async () => await call;

		// The mismatch must surface as a failed call in bounded time (the TLS handshake shares the
		// ConnectTimeout budget), never as a hang or an endless retry loop. The caller observes the
		// raw handshake failure (an IOException either way); its exact text differs per direction
		// and platform, so only the type is asserted.
		await act.Should().ThrowAsync<IOException>($"the mismatch must fail the connect ({scenario})")
			.WaitAsync(TimeSpan.FromSeconds(10));
		transport2!._pendingCalls.Should().BeEmpty("the failed connection attempt must drain the pending call");
	}

	[Fact]
	public async Task CallAsync_ShouldCloseConnectionAndLogWhenClientRejectsServerCertificate()
	{
		using var certificate = CreateSelfSignedCertificate();
		var (port1, port2) = (GetFreePort(), GetFreePort());
		var configuration = new StaticClusterConfiguration
		{
			Nodes = new[]
			{
				CreateNodeConfiguration("node1", port1, 1000, ("echo", 1001)),
				CreateNodeConfiguration("node2", port2, 2000)
			}
		};
		var registry1 = new StaticClusterRegistry(configuration, "node1");
		var registry2 = new StaticClusterRegistry(configuration, "node2");
		var loggerFactory = new RecordingLoggerFactory();

		await using var system1 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry1 },
			transportFactory: sys => new TcpTransport(sys, registry1, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60),
				TlsServerCertificate = certificate
			}, loggerFactory));
		await system1.CreateActorAsync(() => new TlsEchoActor(), "echo",
			new ActorCreationOptions { HandleOverride = new ActorHandle(1001) });

		// The client refuses every server certificate: the handshake aborts on both ends.
		await using var system2 = new ActorSystem(
			options: new ActorSystemOptions { ClusterRegistry = registry2 },
			transportFactory: sys => new TcpTransport(sys, registry2, new TcpTransportOptions
			{
				HeartbeatInterval = TimeSpan.FromSeconds(60),
				UseTls = true,
				RemoteCertificateValidationCallback = (sender, cert, chain, errors) => false
			}, NullLoggerFactory.Instance));

		Func<Task> act = async () => await system2.GetByName("echo").CallAsync<string>(
			new TlsEchoRequest("ping"), TimeSpan.FromSeconds(30));

		// Caller side: the rejected handshake fails the connection with the handshake-failure surface.
		(await act.Should().ThrowAsync<AuthenticationException>().WaitAsync(TimeSpan.FromSeconds(10)))
			.Which.Message.Should().Contain("rejected", "the validation callback's refusal must be observable");

		// Server side: the inbound connection dies inside the TLS handshake and is logged.
		await loggerFactory.WaitForLogAsync("Failed to process incoming connection", TimeSpan.FromSeconds(10));
	}

	/// <summary>
	/// Creates an in-memory self-signed certificate usable as an <see cref="SslStream"/> server
	/// credential. The PFX export/import round-trip detaches the key from the temporary
	/// <see cref="CertificateRequest"/> handle (required on macOS) and yields an owning certificate.
	/// </summary>
	private static X509Certificate2 CreateSelfSignedCertificate()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=skynet-cluster-tls-test", rsa, HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
			DateTimeOffset.UtcNow.AddYears(5));
		return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);
	}

	private static StaticClusterNodeConfiguration CreateNodeConfiguration(string nodeId, int port,
		long handleOffset, params (string Service, long Handle)[] services)
	{
		return new StaticClusterNodeConfiguration
		{
			NodeId = nodeId,
			Host = "127.0.0.1",
			Port = port,
			HandleOffset = handleOffset,
			Services = services.ToDictionary(
				service => service.Service,
				service => service.Handle,
				StringComparer.Ordinal)
		};
	}

	private static int GetFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private sealed class TlsEchoActor : Actor
	{
		protected override Task<object?> ReceiveAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
		{
			return envelope.Payload switch
			{
				TlsEchoRequest request => Task.FromResult<object?>("echo:" + request.Message),
				_ => Task.FromResult<object?>(null)
			};
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed record TlsEchoRequest([property: Key(0)] string Message);
}
