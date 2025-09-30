using System;

namespace Skynet.Extras;

/// <summary>
/// Configuration for the <see cref="DebugConsoleServer"/>.
/// </summary>
public sealed class DebugConsoleOptions
{
	/// <summary>
	/// Gets or sets the IP address the console listener binds to.
	/// </summary>
	public string Host { get; init; } = "127.0.0.1";

	/// <summary>
	/// Gets or sets the TCP port used by the console listener.
	/// </summary>
	public int Port { get; init; } = 4015;

	/// <summary>
	/// Gets or sets the backlog used when binding the TCP listener.
	/// </summary>
	public int Backlog { get; init; } = 128;

	/// <summary>
	/// Gets or sets an optional access token required to authenticate console clients.
	/// </summary>
	public string? AccessToken { get; init; }

	/// <summary>
	/// Gets or sets the idle timeout applied to connected console sessions.
	/// </summary>
	public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);
}
