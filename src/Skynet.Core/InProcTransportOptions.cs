namespace Skynet.Core;

/// <summary>
/// Provides configuration for the <see cref="InProcTransport"/>.
/// </summary>
public sealed record InProcTransportOptions
{
	/// <summary>
	/// Gets the default options instance.
	/// </summary>
	public static InProcTransportOptions Default { get; } = new();

	/// <summary>
	/// Gets or sets a value indicating whether envelopes should skip the transport queue and be delivered immediately.
	/// </summary>
	public bool ShortCircuitLocalDelivery { get; init; } = true;
}
