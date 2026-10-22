using System.Buffers;

namespace VoxelServer;

// Not part of the lesson: the disk. A vanilla server writes an unloaded chunk into a region file and reads it back
// when a player comes near again; here the bytes of each chunk stay in memory, keyed by its position, in arrays rented
// from the shared ArrayPool: a chunk read back gives its array back for the next chunk written.
public sealed class RegionFiles
{
    readonly Dictionary<ChunkPos, (byte[] Bytes, int Length)> _chunks = new();
    byte[]? _read;                                   // the array of the last chunk read, returned on the next read

    // The bytes of the chunk being written, reused for every chunk.
    public Nino.Core.NinoArrayBufferWriter Buffer { get; } = new(16 * 1024);

    public int Count => _chunks.Count;

    // Whether the chunk at `pos` was saved: read back, not generated.
    public bool Has(ChunkPos pos) => _chunks.ContainsKey(pos);

    // Keeps what Buffer holds as the chunk at `pos`, and empties Buffer for the next one.
    public void Write(ChunkPos pos)
    {
        var written = Buffer.WrittenSpan;
        var bytes = ArrayPool<byte>.Shared.Rent(written.Length);
        written.CopyTo(bytes);
        _chunks[pos] = (bytes, written.Length);
        Buffer.ResetWrittenCount();
    }

    // Takes the chunk at `pos` out of the files. The bytes stay valid until the next read.
    public bool TryRead(ChunkPos pos, out ReadOnlySpan<byte> bytes)
    {
        if (_read != null) ArrayPool<byte>.Shared.Return(_read);
        _read = null;
        bytes = default;
        if (!_chunks.Remove(pos, out var saved)) return false;
        _read = saved.Bytes;
        bytes = saved.Bytes.AsSpan(0, saved.Length);
        return true;
    }
}
