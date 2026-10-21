namespace VoxelServer;

// Not part of the lesson: the disk. A vanilla server writes an unloaded chunk into a region file and reads it back
// when a player comes near again; here the bytes of each chunk stay in memory, keyed by its position.
public sealed class RegionFiles
{
    readonly Dictionary<ChunkPos, byte[]> _chunks = new();

    // The bytes of the chunk being written, reused for every chunk.
    public Nino.Core.NinoArrayBufferWriter Buffer { get; } = new(16 * 1024);

    public int Count => _chunks.Count;

    // Keeps what Buffer holds as the chunk at `pos`, and empties Buffer for the next one.
    public void Write(ChunkPos pos)
    {
        _chunks[pos] = Buffer.WrittenSpan.ToArray();
        Buffer.ResetWrittenCount();
    }

    public bool TryRead(ChunkPos pos, out byte[] bytes) => _chunks.Remove(pos, out bytes!);
}
