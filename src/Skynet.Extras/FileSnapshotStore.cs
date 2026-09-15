using System.Globalization;
using MessagePack;
using Skynet.Core.Persistence;

namespace Skynet.Extras;

/// <summary>
/// File-system sample implementation of <see cref="IActorSnapshotStore"/>. Snapshots are stored
/// one file per slot under an actor-key directory; writes go to a unique temporary file that is
/// atomically moved into place, so a crash or a concurrent writer can never leave a partially
/// written snapshot behind.
/// </summary>
/// <remarks>
/// <para>
/// On-disk layout (all names relative to the root directory):
/// <code>
/// &lt;root&gt;/&lt;url-encoded actor key&gt;/&lt;generation, 19 digits&gt;.snapshot
/// </code>
/// The actor key is percent-encoded with <see cref="Uri.EscapeDataString"/>, so keys may contain
/// any non-empty text (including non-ASCII, slashes and separators) and still map to exactly one
/// directory. Each snapshot file starts with a 7-byte magic (<c>SKSNAP1</c>) and a 4-byte
/// little-endian body length, followed by the MessagePack-serialized
/// <see cref="ActorSnapshot"/> envelope (actor key, generation, creation time, opaque payload).
/// The frame makes truncation and trailing garbage deterministic, typed errors instead of
/// silent misreads.
/// </para>
/// <para>
/// This is a sample/quality-of-life store for single-node deployments: it provides atomicity but
/// neither compression, encryption, schema migration for user payloads, nor cross-node locking.
/// </para>
/// </remarks>
public sealed class FileSnapshotStore : IActorSnapshotStore
{
	private const string FileExtension = ".snapshot";
	private const string TemporarySuffix = ".tmp";
	private const int GenerationDigits = 19;
	private const int LengthPrefixBytes = 4;
	private const int HeaderBytes = 7;
	private const int MaxEncodedKeyLength = 200;

	/// <summary>
	/// On-disk format identifier: ASCII "SKSNAP" plus a single format version byte ('1'). The
	/// magic doubles as a checksum-free integrity anchor: truncated files are rejected by the
	/// explicit length check, arbitrary corruption by the magic mismatch.
	/// </summary>
	private static ReadOnlySpan<byte> Magic => "SKSNAP1"u8;

	private readonly string _rootDirectory;

	/// <summary>
	/// Initializes a new store rooted at the given directory. The directory is created lazily on
	/// the first save.
	/// </summary>
	/// <param name="rootDirectory">Base directory holding one subdirectory per actor key.</param>
	/// <exception cref="ArgumentException">Thrown when <paramref name="rootDirectory"/> is null or whitespace.</exception>
	public FileSnapshotStore(string rootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
		_rootDirectory = Path.GetFullPath(rootDirectory);
	}

	/// <summary>
	/// Gets the absolute path of the directory that holds the snapshot files.
	/// </summary>
	public string RootDirectory => _rootDirectory;

	/// <inheritdoc />
	public async Task SaveAsync(ActorSnapshot snapshot, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ValidateActorKey(snapshot.ActorKey);
		if (snapshot.Generation < ActorSnapshotKey.Unversioned)
		{
			throw new ArgumentOutOfRangeException(nameof(snapshot), "The snapshot generation must not be negative.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		var directory = Path.Combine(_rootDirectory, EncodeActorKey(snapshot.ActorKey));
		Directory.CreateDirectory(directory);
		var finalPath = Path.Combine(directory, GenerationFileName(snapshot.Generation));
		var temporaryPath = Path.Combine(directory,
			$"{snapshot.Generation:D19}.{Guid.NewGuid():N}{TemporarySuffix}");

		var body = MessagePackSerializer.Serialize(snapshot, cancellationToken: cancellationToken);
		var frame = new byte[HeaderBytes + LengthPrefixBytes + body.Length];
		Magic.CopyTo(frame);
		BitConverter.TryWriteBytes(frame.AsSpan(HeaderBytes, LengthPrefixBytes), body.Length);
		body.CopyTo(frame, HeaderBytes + LengthPrefixBytes);

		try
		{
			var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
				8192, useAsync: true);
			await using (file.ConfigureAwait(false))
			{
				await file.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
				await file.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			// Same-directory rename: atomic on POSIX and on Windows (MoveFileEx with
			// REPLACE_EXISTING). Concurrent saves each stage their own temporary file, so the
			// final name always resolves to one complete snapshot — last writer wins.
			File.Move(temporaryPath, finalPath, overwrite: true);
		}
		catch
		{
			try
			{
				File.Delete(temporaryPath);
			}
			catch
			{
				// Best-effort cleanup of the abandoned temporary file; the original failure is
				// more relevant than a cleanup error.
			}

			throw;
		}
	}

	/// <inheritdoc />
	/// <exception cref="SnapshotCorruptedException">Thrown when a snapshot file exists for the key but cannot be read back (truncated, trailing bytes, wrong format or unparseable content).</exception>
	public async Task<ActorSnapshot?> LoadAsync(ActorSnapshotKey key, CancellationToken cancellationToken = default)
	{
		ValidateActorKey(key.ActorKey);
		cancellationToken.ThrowIfCancellationRequested();

		var directory = Path.Combine(_rootDirectory, EncodeActorKey(key.ActorKey));
		var generation = key.Generation ?? FindLatestGeneration(directory);
		if (generation is null)
		{
			return null;
		}

		var path = Path.Combine(directory, GenerationFileName(generation.Value));
		if (!File.Exists(path))
		{
			return null;
		}

		// FileShare.Delete keeps concurrent saves able to replace the file while it is read.
		var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
			8192, useAsync: true);
		await using (file.ConfigureAwait(false))
		{
			var length = file.Length;
			if (length < HeaderBytes + LengthPrefixBytes)
			{
				throw new SnapshotCorruptedException(
					$"Snapshot file '{path}' is truncated: {length} bytes is shorter than the {HeaderBytes + LengthPrefixBytes}-byte header.");
			}

			var bytes = new byte[length];
			await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
			return Parse(path, bytes, cancellationToken);
		}
	}

	/// <summary>
	/// Validates the framing and deserializes the stored envelope, converting every structural
	/// problem into a <see cref="SnapshotCorruptedException"/>.
	/// </summary>
	private static ActorSnapshot Parse(string path, byte[] bytes, CancellationToken cancellationToken)
	{
		if (!bytes.AsSpan(0, HeaderBytes).SequenceEqual(Magic))
		{
			throw new SnapshotCorruptedException($"Snapshot file '{path}' does not carry the SKSNAP1 file magic.");
		}

		var bodyLength = BitConverter.ToInt32(bytes, HeaderBytes);
		if (bodyLength < 0 || bytes.Length != HeaderBytes + LengthPrefixBytes + bodyLength)
		{
			throw new SnapshotCorruptedException(
				$"Snapshot file '{path}' has an inconsistent length: header declares {bodyLength} body bytes, file holds {bytes.Length} total bytes.");
		}

		ActorSnapshot snapshot;
		try
		{
			snapshot = MessagePackSerializer.Deserialize<ActorSnapshot>(bytes.AsMemory(HeaderBytes + LengthPrefixBytes),
				cancellationToken: cancellationToken);
		}
		catch (MessagePackSerializationException exception)
		{
			throw new SnapshotCorruptedException(
				$"Snapshot file '{path}' does not contain a valid MessagePack actor snapshot.", exception);
		}

		// The envelope is self-describing; a mismatch means the file was moved or tampered with.
		if (snapshot.Generation < ActorSnapshotKey.Unversioned)
		{
			throw new SnapshotCorruptedException($"Snapshot file '{path}' declares a negative generation {snapshot.Generation}.");
		}

		return snapshot;
	}

	/// <summary>
	/// Scans the actor's directory for snapshot files and returns the highest generation, or
	/// <see langword="null"/> when the directory does not exist or holds no snapshot.
	/// </summary>
	private static long? FindLatestGeneration(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return null;
		}

		long? latest = null;
		foreach (var path in Directory.EnumerateFiles(directory, "*" + FileExtension))
		{
			var name = Path.GetFileName(path);
			if (name.Length != GenerationDigits + FileExtension.Length ||
				!long.TryParse(name.AsSpan(0, GenerationDigits), NumberStyles.None, CultureInfo.InvariantCulture,
					out var generation))
			{
				continue;
			}

			if (latest is null || generation > latest.Value)
			{
				latest = generation;
			}
		}

		return latest;
	}

	private static string GenerationFileName(long generation)
	{
		return $"{generation:D19}{FileExtension}";
	}

	private static string EncodeActorKey(string actorKey)
	{
		var encoded = Uri.EscapeDataString(actorKey);
		if (encoded.Length > MaxEncodedKeyLength)
		{
			throw new ArgumentException(
				$"The URL-encoded actor key is {encoded.Length} characters long and exceeds the {MaxEncodedKeyLength}-character file-name budget. " +
				"Use a shorter actor key.",
				nameof(actorKey));
		}

		return encoded;
	}

	private static void ValidateActorKey(string actorKey)
	{
		ArgumentException.ThrowIfNullOrEmpty(actorKey);
	}
}
