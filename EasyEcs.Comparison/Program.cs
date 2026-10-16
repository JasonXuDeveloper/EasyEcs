using System;
using BenchmarkDotNet.Running;
using EcsComparison.ArchPort;
using EcsComparison.DefaultEcsPort;
using EcsComparison.Features;
using EcsComparison.FrifloPort;

namespace EcsComparison;

public static class Program
{
    /// <summary>
    /// BenchmarkDotNet by default; <c>--check [ticks]</c> runs every library for a few hundred ticks at both entity
    /// counts, prints the counters and verifies they match EasyEcs, then runs the setups of the feature comparisons,
    /// which check each library's result (no measurement).
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "--check")
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return 0;
        }

        int ticks = args.Length > 1 ? int.Parse(args[1]) : 300;
        bool ok = true;
        foreach (int entities in new[] { 10_000, 100_000 })
        {
            Console.WriteLine($"{entities:N0} entities, {ticks} ticks");
            Counters reference;
            using (var easy = new EasyEcsSimulation(entities))
            {
                reference = Run(easy, ticks);
                PrintAllocations(easy);
            }
            ISimulation[] others = [new ArchSimulation(entities), new FrifloSimulation(entities), new DefaultEcsSimulation(entities)];
            foreach (var sim in others)
            {
                using (sim)
                {
                    var c = Run(sim, ticks);
                    try { Check.Matches(reference, c, sim.Library); }
                    catch (InvalidOperationException e) { ok = false; Console.WriteLine("  MISMATCH " + e.Message); }
                    PrintAllocations(sim);
                }
            }
        }
        ok &= CheckFeatures();
        return ok ? 0 : 1;
    }

    /// <summary>The feature comparisons check every library's result in their setups: run each of them once.</summary>
    private static bool CheckFeatures()
    {
        var changes = new ChangeTrackingComparison();
        var index = new IndexComparison { Trades = 100 };
        (string Name, Action Setup, Action Cleanup)[] setups =
        [
            ("ChangeTracking EasyEcs", changes.SetupEasyEcs, changes.Cleanup),
            ("ChangeTracking Friflo", changes.SetupFriflo, changes.Cleanup),
            ("ChangeTracking Arch", changes.SetupArch, changes.Cleanup),
            ("ChangeTracking DefaultEcs", changes.SetupDefaultEcs, changes.Cleanup),
            ("Index EasyEcs", index.SetupEasyEcs, index.Cleanup),
            ("Index Friflo", index.SetupFriflo, index.Cleanup),
            ("Index DefaultEcs", index.SetupDefaultEcs, index.Cleanup),
        ];
        bool ok = true;
        foreach (var (name, setup, cleanup) in setups)
        {
            try
            {
                setup();
                Console.WriteLine($"  {name,-26} ok");
            }
            catch (InvalidOperationException e)
            {
                ok = false;
                Console.WriteLine($"  {name,-26} MISMATCH {e.Message}");
            }
            finally
            {
                cleanup();
            }
        }

        return ok;
    }

    /// <summary>Bytes allocated per tick after warm-up (a sanity check only; BenchmarkDotNet reports the real figure).</summary>
    private static void PrintAllocations(ISimulation sim, int ticks = 50)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ticks; i++) sim.Tick();
        Console.WriteLine($"  {"",-10} ~{(GC.GetAllocatedBytesForCurrentThread() - before) / ticks:N0} B allocated per tick");
    }

    private static Counters Run(ISimulation sim, int ticks)
    {
        var c = Check.WarmUp(sim, ticks);
        Console.WriteLine($"  {sim.Library,-10} {c}");
        return c;
    }
}
