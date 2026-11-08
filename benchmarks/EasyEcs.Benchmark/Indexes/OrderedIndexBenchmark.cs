using System;
using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// A leaderboard: 100,000 players with a <c>Score</c>; per operation, 1,000 scores change and the top 10 and the
/// players of one score bracket are read. Through the ordered index (<c>Descending</c>, <c>Range</c>: the keys stay
/// sorted as they change) and through a scan keeping the best 10 by hand. Steady state allocates 0 bytes.
/// </summary>
[MemoryDiagnoser]
public partial class OrderedIndexBenchmark
{
    [Params(100_000)] public int Players;

    private World _indexed, _scanned;
    private Entity[] _indexedPlayers, _scannedPlayers;
    private ValueIndex<RankedScore, int> _scores;
    private readonly Entity[] _top = new Entity[10];
    private readonly int[] _topScores = new int[10];
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _indexed = new World(new WorldOptions { InitialEntityCapacity = Players });
        _scanned = new World(new WorldOptions { InitialEntityCapacity = Players });
        _indexedPlayers = new Entity[Players];
        _scannedPlayers = new Entity[Players];
        for (int i = 0; i < Players; i++)
        {
            _indexedPlayers[i] = _indexed.CreateEntity(new RankedScore(i % 5_000));
            _scannedPlayers[i] = _scanned.CreateEntity(new PlainScore(i % 5_000));
        }

        _scores = _indexed.Index<RankedScore, int>();
        for (int i = 0; i < 10; i++)
        {
            Ordered();
            Scan();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _indexed.Dispose();
        _scanned.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Ordered()
    {
        _next++;
        for (int i = 0; i < 1_000; i++) _indexedPlayers[Pick(i)].Set(new RankedScore(NewScore(i)));
        int count = 0, total = 0;
        foreach (var player in _scores.Descending())
        {
            _top[count] = player;
            if (++count == _top.Length) break;
        }

        foreach (var player in _scores.Range(2_000, 2_010)) total++;
        return total + count;
    }

    [Benchmark]
    public int Scan()
    {
        _next++;
        for (int i = 0; i < 1_000; i++) _scannedPlayers[Pick(i)].Get<PlainScore>().Value = NewScore(i);
        int count = 0, total = 0;
        foreach (var item in _scanned.Query<PlainScore>())
        {
            int score = item.Component1.Value;
            if (score >= 2_000 && score <= 2_010) total++;
            if (count == _top.Length && score <= _topScores[count - 1]) continue;
            int at = Math.Min(count, _top.Length - 1);                           // insertion into the best 10
            while (at > 0 && _topScores[at - 1] < score)
            {
                _topScores[at] = _topScores[at - 1];
                _top[at] = _top[at - 1];
                at--;
            }

            _topScores[at] = score;
            _top[at] = item.Entity;
            if (count < _top.Length) count++;
        }

        return total + count;
    }

    private int Pick(int i) => (int)((uint)(_next * 7919 + i * 104729) % (uint)Players);

    private int NewScore(int i) => (int)((uint)(_next * 31 + i * 17) % 5_000);

    public partial record struct RankedScore([LookupKey] int Value) : IComponent;
    public record struct PlainScore(int Value) : IComponent;
}
