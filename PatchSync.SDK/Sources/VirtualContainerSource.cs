using System.Collections.Frozen;
using System.Security.Cryptography;
using PatchSync.Common.Chunking;
using PatchSync.Common.Containers;
using PatchSync.Common.Hashing;
using PatchSync.SDK.Containers;

namespace PatchSync.SDK.Sources;

/// <summary>
/// Location of an entry within a container.
/// </summary>
/// <param name="ContainerPath">Path to the container file.</param>
/// <param name="EntryId">Entry identifier.</param>
/// <param name="Offset">Byte offset of entry data within container.</param>
/// <param name="Size">Size of entry data.</param>
/// <param name="Compression">Compression type of the entry.</param>
public readonly record struct EntryLocation(
    string ContainerPath,
    string EntryId,
    long Offset,
    int Size,
    ContainerCompression Compression);

/// <summary>
/// Location of a chunk within a container entry.
/// </summary>
/// <param name="ContainerPath">Path to the container file.</param>
/// <param name="EntryId">Entry identifier containing this chunk.</param>
/// <param name="EntryOffset">Byte offset of entry within container.</param>
/// <param name="ChunkOffset">Byte offset of chunk within entry.</param>
/// <param name="ChunkSize">Size of the chunk.</param>
public readonly record struct SubEntryChunkLocation(
    string ContainerPath,
    string EntryId,
    long EntryOffset,
    long ChunkOffset,
    int ChunkSize);

/// <summary>
/// A chunk source backed by a parsed container file.
/// Indexes both entry-level and sub-entry CDC chunks for fast lookup.
/// </summary>
public sealed class VirtualContainerSource : IChunkSource
{
    private readonly string _containerPath;
    private readonly ContainerInfo _containerInfo;
    private readonly IContainerHandler _handler;

    // Entry-level index: hash -> entry location
    private readonly FrozenDictionary<Hash256, EntryLocation> _entryIndex;

    // Sub-entry chunk index: hash -> chunk location within entry
    private readonly FrozenDictionary<Hash256, ChunkLocation> _chunkIndex;

    private readonly int _totalChunkCount;
    private readonly long _totalSize;

    private VirtualContainerSource(
        string containerPath,
        ContainerInfo containerInfo,
        IContainerHandler handler,
        FrozenDictionary<Hash256, EntryLocation> entryIndex,
        FrozenDictionary<Hash256, ChunkLocation> chunkIndex,
        int totalChunkCount,
        long totalSize)
    {
        _containerPath = containerPath;
        _containerInfo = containerInfo;
        _handler = handler;
        _entryIndex = entryIndex;
        _chunkIndex = chunkIndex;
        _totalChunkCount = totalChunkCount;
        _totalSize = totalSize;
    }

    /// <summary>
    /// Path to the container file.
    /// </summary>
    public string ContainerPath => _containerPath;

    /// <summary>
    /// The parsed container info.
    /// </summary>
    public ContainerInfo ContainerInfo => _containerInfo;

    /// <summary>
    /// The container handler.
    /// </summary>
    public IContainerHandler Handler => _handler;

    /// <summary>
    /// Number of indexed entries.
    /// </summary>
    public int EntryCount => _entryIndex.Count;

    /// <inheritdoc />
    public int ChunkCount => _totalChunkCount;

    /// <inheritdoc />
    public long TotalSize => _totalSize;

    /// <inheritdoc />
    public bool TryGetChunk(Hash256 hash, out ChunkLocation location)
    {
        // Try sub-entry chunks first (more specific match)
        if (_chunkIndex.TryGetValue(hash, out location))
        {
            return true;
        }

        // Fall back to entry-level (entire entry matches)
        if (_entryIndex.TryGetValue(hash, out var entryLocation))
        {
            // Convert entry location to chunk location
            location = new ChunkLocation(
                entryLocation.ContainerPath,
                entryLocation.Offset,
                entryLocation.Size);
            return true;
        }

        location = default;
        return false;
    }

    /// <summary>
    /// Tries to find an entry by its stored hash.
    /// </summary>
    /// <param name="storedHash">SHA256 hash of the stored (raw) bytes.</param>
    /// <param name="location">The entry location if found.</param>
    /// <returns>True if the entry was found.</returns>
    public bool TryGetEntry(Hash256 storedHash, out EntryLocation location)
    {
        return _entryIndex.TryGetValue(storedHash, out location);
    }

    /// <summary>
    /// Gets all entry hashes in this container.
    /// </summary>
    public IEnumerable<Hash256> GetEntryHashes() => _entryIndex.Keys;

    /// <summary>
    /// Gets all sub-entry chunk hashes in this container.
    /// </summary>
    public IEnumerable<Hash256> GetChunkHashes() => _chunkIndex.Keys;

    /// <summary>
    /// Builds a virtual container source from a local container file.
    /// </summary>
    /// <param name="containerPath">Path to the container file.</param>
    /// <param name="handler">Container handler for this format.</param>
    /// <param name="chunker">Chunker for sub-entry CDC.</param>
    /// <param name="options">Chunking options.</param>
    /// <param name="cdcThreshold">Minimum entry size for CDC chunking (default 64KB).</param>
    /// <returns>A virtual container source with indexed entries and chunks.</returns>
    public static VirtualContainerSource Build(
        string containerPath,
        IContainerHandler handler,
        IChunker chunker,
        ChunkingOptions options,
        int cdcThreshold = 65536)
    {
        if (!File.Exists(containerPath))
            throw new FileNotFoundException("Container file not found", containerPath);

        using var stream = new FileStream(
            containerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);

        // Parse container
        var containerInfo = handler.Parse(stream);

        var entryBuilder = new Dictionary<Hash256, EntryLocation>();
        var chunkBuilder = new Dictionary<Hash256, ChunkLocation>();
        int totalChunkCount = 0;
        long totalSize = 0;

        // Index each entry
        foreach (var entry in containerInfo.Entries)
        {
            // Compute stored hash for entry
            stream.Position = entry.Offset;
            var storedHash = ComputeHash(stream, entry.StoredSize);
            entry.StoredHash = storedHash;

            var hash = new Hash256(storedHash);
            var entryLocation = new EntryLocation(
                containerPath,
                entry.EntryId,
                entry.Offset,
                entry.StoredSize,
                entry.Compression);

            // Index entry by stored hash
            entryBuilder.TryAdd(hash, entryLocation);
            totalChunkCount++;
            totalSize += entry.StoredSize;

            // CDC chunk large uncompressed entries
            if (entry.IsLargeUncompressed && entry.StoredSize >= cdcThreshold)
            {
                stream.Position = entry.Offset;

                // Create a bounded stream for this entry
                var boundedStream = new BoundedStream(stream, entry.StoredSize);

                foreach (var boundary in chunker.Chunk(boundedStream, options))
                {
                    var chunkHash = new Hash256(boundary.Hash);

                    // Chunk location is absolute offset in container
                    var chunkLocation = new ChunkLocation(
                        containerPath,
                        entry.Offset + boundary.Offset,
                        boundary.Length);

                    chunkBuilder.TryAdd(chunkHash, chunkLocation);
                    totalChunkCount++;
                }
            }
        }

        return new VirtualContainerSource(
            containerPath,
            containerInfo,
            handler,
            entryBuilder.ToFrozenDictionary(),
            chunkBuilder.ToFrozenDictionary(),
            totalChunkCount,
            totalSize);
    }

    /// <summary>
    /// Builds a virtual container source asynchronously.
    /// </summary>
    public static Task<VirtualContainerSource> BuildAsync(
        string containerPath,
        IContainerHandler handler,
        IChunker chunker,
        ChunkingOptions options,
        int cdcThreshold = 65536,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(containerPath, handler, chunker, options, cdcThreshold);
        }, cancellationToken);
    }

    private static byte[] ComputeHash(Stream stream, int length)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[Math.Min(length, 81920)];
        int remaining = length;

        while (remaining > 0)
        {
            int toRead = Math.Min(remaining, buffer.Length);
            int bytesRead = stream.Read(buffer, 0, toRead);
            if (bytesRead == 0)
                throw new EndOfStreamException("Unexpected end of stream while hashing entry");

            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            remaining -= bytesRead;
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha256.Hash!;
    }

    /// <summary>
    /// A stream that limits reads to a specified length.
    /// </summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _position;

        public BoundedStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
                return 0;

            int toRead = (int)Math.Min(count, remaining);
            int bytesRead = _inner.Read(buffer, offset, toRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
