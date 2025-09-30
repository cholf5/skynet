using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Skynet.Extras;

/// <summary>
/// Hosts a simple TCP-based console for interacting with an <see cref="ActorSystem"/>.
/// </summary>
public sealed class DebugConsoleServer : IAsyncDisposable
{
	private readonly IDebugConsoleActorGateway _gateway;
	private readonly DebugConsoleOptions _options;
	private readonly ILogger _logger;
	private readonly DebugConsoleCommandProcessor _processor;
	private readonly CancellationTokenSource _cts = new();
	private TcpListener? _listener;
	private Task? _acceptLoop;
	private bool _disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="DebugConsoleServer"/> class.
	/// </summary>
	public DebugConsoleServer(IDebugConsoleActorGateway gateway, DebugConsoleOptions? options = null, ILogger<DebugConsoleServer>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(gateway);
		_gateway = gateway;
		_options = options ?? new DebugConsoleOptions();
		_logger = logger ?? NullLogger<DebugConsoleServer>.Instance;
		_processor = new DebugConsoleCommandProcessor(gateway);
	}

	/// <summary>
	/// Starts listening for console connections.
	/// </summary>
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();
		if (_listener is not null)
		{
			throw new InvalidOperationException("The debug console server is already running.");
		}

		var address = ParseAddress(_options.Host);
		_listener = new TcpListener(address, _options.Port);
		_listener.Start(_options.Backlog);
		_acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
		_logger.LogInformation("Debug console listening on {Host}:{Port}.", address, _options.Port);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Stops listening for console connections and disconnects all clients.
	/// </summary>
	public async Task StopAsync()
	{
		if (_listener is null)
		{
			return;
		}

		_cts.Cancel();
		try
		{
			_listener.Stop();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Error while stopping debug console listener.");
		}

		if (_acceptLoop is not null)
		{
			try
			{
				await _acceptLoop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_acceptLoop = null;
		_listener = null;
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient? client = null;
			try
			{
				client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
				_ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				client?.Dispose();
				break;
			}
			catch (Exception ex)
			{
				client?.Dispose();
				_logger.LogError(ex, "Debug console accept loop encountered an error.");
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		{
			linkedCts.CancelAfter(_options.IdleTimeout);
			NetworkStream stream;
			try
			{
				stream = client.GetStream();
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Failed to obtain client stream.");
				return;
			}

			await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
			using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
			await writer.WriteLineAsync("Skynet Debug Console").ConfigureAwait(false);
			await writer.WriteLineAsync("Type 'help' for available commands.").ConfigureAwait(false);

			if (!string.IsNullOrEmpty(_options.AccessToken))
			{
				if (!await AuthenticateAsync(reader, writer, linkedCts.Token).ConfigureAwait(false))
				{
					return;
				}
			}

			while (!linkedCts.IsCancellationRequested)
			{
				await writer.WriteAsync("> ").ConfigureAwait(false);
				var line = await reader.ReadLineAsync().ConfigureAwait(false);
				if (line is null)
				{
					break;
				}

				linkedCts.CancelAfter(_options.IdleTimeout);
				DebugConsoleCommandResult result;
				try
				{
					result = await _processor.ExecuteAsync(line, linkedCts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					await writer.WriteLineAsync("Session timed out.").ConfigureAwait(false);
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to execute console command.");
					await writer.WriteLineAsync("Command failed: " + ex.Message).ConfigureAwait(false);
					continue;
				}

				if (!string.IsNullOrWhiteSpace(result.Response))
				{
					await writer.WriteLineAsync(result.Response).ConfigureAwait(false);
				}

				if (result.ShouldExit)
				{
					break;
				}
			}
		}
	}

	private async Task<bool> AuthenticateAsync(StreamReader reader, StreamWriter writer, CancellationToken cancellationToken)
	{
		await writer.WriteLineAsync("Authentication required. Use: auth <token>").ConfigureAwait(false);
		while (!cancellationToken.IsCancellationRequested)
		{
			await writer.WriteAsync("> ").ConfigureAwait(false);
			var line = await reader.ReadLineAsync().ConfigureAwait(false);
			if (line is null)
			{
				return false;
			}

			var trimmed = line.Trim();
			if (trimmed.StartsWith("auth ", StringComparison.OrdinalIgnoreCase))
			{
				var provided = trimmed.Substring(5).Trim();
				if (string.Equals(provided, _options.AccessToken, StringComparison.Ordinal))
				{
					await writer.WriteLineAsync("Authentication successful.").ConfigureAwait(false);
					return true;
				}

				await writer.WriteLineAsync("Authentication failed.").ConfigureAwait(false);
			}
			else
			{
				await writer.WriteLineAsync("Authentication required. Use: auth <token>").ConfigureAwait(false);
			}
		}

		return false;
	}

	private static IPAddress ParseAddress(string host)
	{
		if (string.Equals(host, "*", StringComparison.Ordinal))
		{
			return IPAddress.Any;
		}

		if (IPAddress.TryParse(host, out var address))
		{
			return address;
		}

		return IPAddress.Loopback;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(DebugConsoleServer));
		}
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
		_cts.Dispose();
	}
}
