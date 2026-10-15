using System;
using BenchmarkDotNet.Running;
using EcsComparison.ArchPort;
using EcsComparison.DefaultEcsPort;
using EcsComparison.FrifloPort;

namespace EcsComparison;

public static class Program
{
    /// <summary>
    /// BenchmarkDotNet by default; <c>--check [ticks]</c> runs every library for a few hundred ticks at both entity
    /// counts, prints the counters and verifies they match EasyEcs (no measurement).
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
        return ok ? 0 : 1;
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
