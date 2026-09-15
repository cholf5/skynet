namespace Skynet.Extras;

/// <summary>
/// Thrown by <see cref="FileSnapshotStore"/> when a stored snapshot exists but cannot be read
/// back: the file is truncated, carries unexpected trailing bytes, does not match the store's
/// on-disk format, or its content does not deserialize. Callers can catch this concrete type to
/// distinguish "corrupt data" from "no data" (<see langword="null"/> return) and from transient
/// I/O failures.
/// </summary>
public sealed class SnapshotCorruptedException : Exception
{
	/// <summary>
	/// Initializes the exception with a message describing why the snapshot is unreadable.
	/// </summary>
	/// <param name="message">The human-readable description of the corruption.</param>
	public SnapshotCorruptedException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Initializes the exception with a message and the underlying deserialization failure.
	/// </summary>
	/// <param name="message">The human-readable description of the corruption.</param>
	/// <param name="innerException">The exception raised while parsing the snapshot content.</param>
	public SnapshotCorruptedException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
