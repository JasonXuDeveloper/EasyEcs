using BenchmarkDotNet.Attributes;

namespace EasyEcs.Benchmark;

/// <summary>
/// The halo exchange alone, the share of a stencil pass that copies the neighbors' edges into each plate's texture:
/// a halo of 2 on 16 x 16 float plates (WavePlot, the Laplacian's tile: each side run is 8 bytes) and a halo of 1
/// (BlurPlot, 4 bytes, corners included). What TileLayout.Copy's runs under 16 bytes cost.
/// </summary>
[MemoryDiagnoser]
public class TileExchangeBenchmark
{
    [Params(1, 4)] public int Threads;

    [Params(Plot.Default, 512)] public int Side;

    private World _halo2, _halo1;

    [GlobalSetup]
    public void Setup()
    {
        Plot.Side = Side;
        _halo2 = TileWorlds.Warm<WavePlot>(Threads, p => new WavePlot(p), next: false);
        _halo2.AddSystem<ExchangeSystems.Halo2System>();
        _halo1 = TileWorlds.Warm<BlurPlot>(Threads, p => new BlurPlot(p), next: false);
        _halo1.AddSystem<ExchangeSystems.Halo1System>();
        for (int i = 0; i < 3; i++)
        {
            Halo2();
            Halo1();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _halo2.Dispose();
        _halo1.Dispose();
    }

    [Benchmark] public void Halo2() => _halo2.Update();
    [Benchmark] public void Halo1() => _halo1.Update();
}

internal static class ExchangeSystems
{
    /// <summary>Exchanges the halo of every Laplacian plate (halo 2) and reads nothing else.</summary>
    [EcsSystem]
    public ref struct Halo2System
    {
        [ForEach(Parallel = true)]
        private static void Exchange(Tile<Warmth, WavePlot> warmth) => warmth.ExchangeHalo();
    }

    /// <summary>Exchanges the halo of every blur plate (halo 1, its corners included) and reads nothing else.</summary>
    [EcsSystem]
    public ref struct Halo1System
    {
        [ForEach(Parallel = true)]
        private static void Exchange(Tile<Warmth, BlurPlot> warmth) => warmth.ExchangeHalo();
    }
}
