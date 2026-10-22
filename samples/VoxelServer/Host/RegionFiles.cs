using Microsoft.Win32.SafeHandles;

namespace VoxelServer;

// Not part of the lesson: the disk. As a vanilla server does, the chunks nobody is near go into a region file in
// sectors of 4 KiB, each chunk in a run of sectors, found through a table of where each chunk starts; a chunk read back
// frees its sectors for the next chunk written. The file is a real one, deleted on close, written and read with
// RandomAccess through one buffer each way, so the disk allocates nothing per chunk. The table and the map of free
// sectors are sized for the server's capacity and grow (rarely) only when the players explore beyond it.
public sealed class RegionFiles : IDisposable
{
    const int Sector = 4096;

    readonly SafeFileHandle _file = File.OpenHandle(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    readonly Dictionary<ChunkPos, (int Sector, int Length)> _chunks;
    ulong[] _used;                                   // a bit per sector: taken by a chunk
    byte[] _read = new byte[4 * Sector];             // the bytes of the last chunk read

    public RegionFiles(int chunks)
    {
        _chunks = new Dictionary<ChunkPos, (int, int)>(chunks);
        _used = new ulong[Math.Max(1, chunks * 8 / 64)];   // eight sectors a chunk on average
    }

    // The bytes of the chunk being written, reused for every chunk.
    public Nino.Core.NinoArrayBufferWriter Buffer { get; } = new(16 * 1024);

    public int Count => _chunks.Count;

    // Whether the chunk at `pos` was saved: read back, not generated.
    public bool Has(ChunkPos pos) => _chunks.ContainsKey(pos);

    // Writes what Buffer holds as the chunk at `pos` into the first free run of sectors, and empties Buffer.
    public void Write(ChunkPos pos)
    {
        var written = Buffer.WrittenSpan;
        int sector = Take((written.Length + Sector - 1) / Sector);
        RandomAccess.Write(_file, written, (long)sector * Sector);
        _chunks[pos] = (sector, written.Length);
        Buffer.ResetWrittenCount();
    }

    // Takes the chunk at `pos` out of the file, freeing its sectors. The bytes stay valid until the next read.
    public bool TryRead(ChunkPos pos, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (!_chunks.Remove(pos, out var saved)) return false;
        if (_read.Length < saved.Length) _read = new byte[saved.Length * 2];
        RandomAccess.Read(_file, _read.AsSpan(0, saved.Length), (long)saved.Sector * Sector);
        Free(saved.Sector, (saved.Length + Sector - 1) / Sector);
        bytes = _read.AsSpan(0, saved.Length);
        return true;
    }

    public void Dispose() => _file.Dispose();

    // The first run of `count` free sectors, marked taken; the map doubles when no run fits.
    int Take(int count)
    {
        int run = 0;
        for (int s = 0; ; s++)
        {
            if (s == _used.Length * 64) Array.Resize(ref _used, _used.Length * 2);
            if ((_used[s >> 6] & 1UL << s) != 0) { run = 0; continue; }
            if (++run < count) continue;
            int first = s - count + 1;
            for (int i = first; i <= s; i++) _used[i >> 6] |= 1UL << i;
            return first;
        }
    }

    void Free(int first, int count)
    {
        for (int i = first; i < first + count; i++) _used[i >> 6] &= ~(1UL << i);
    }
}
