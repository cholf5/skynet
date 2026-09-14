namespace Skynet.Core;

/// <summary>
/// Shared helper for transport dead-peer detection: resolves the effective dead-node grace period
/// from the transport options. Both <c>TcpTransport</c> (Skynet.Cluster) and <c>KcpTransport</c>
/// (Skynet.Transport.Kcp) apply the same rule so their dead-peer detection semantics stay
/// interchangeable.
/// </summary>
public static class DeadNodeGracePeriod
{
	/// <summary>
	/// Resolves the effective dead-node grace period: an explicitly configured grace period wins;
	/// otherwise it defaults to three times the heartbeat interval so dead-peer detection is active
	/// by default instead of requiring explicit configuration. Returns <see cref="TimeSpan.Zero"/>
	/// when dead-peer detection is disabled (no configured grace period and no heartbeat).
	/// </summary>
	/// <param name="configuredGracePeriod">The explicitly configured grace period, if any.</param>
	/// <param name="heartbeatInterval">The configured heartbeat interval.</param>
	/// <returns>The effective grace period, or <see cref="TimeSpan.Zero"/> to disable detection.</returns>
	public static TimeSpan Resolve(TimeSpan configuredGracePeriod, TimeSpan heartbeatInterval)
	{
		if (configuredGracePeriod > TimeSpan.Zero)
		{
			return configuredGracePeriod;
		}

		return heartbeatInterval > TimeSpan.Zero
			? TimeSpan.FromTicks(3 * heartbeatInterval.Ticks)
			: TimeSpan.Zero;
	}
}
