// Runs the [Benchmark] methods of one benchmark class (all, or those named) often enough for tiered compilation to
// promote their runners to Tier1, with the class's integer [Params] set to the size given and every case of the others
// (tools/Shared/BenchmarkRunner.cs; the class as KernelBenchmark or Kernel). tools/loopbound.sh runs it with
// DOTNET_JitDisasm and reads the loops of the Tier1 listings.
//
//   LoopBound <class> <rows> <calls> [method,method,...]
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: LoopBound <benchmark class> <rows> <calls> [methods, comma separated]");
    return 2;
}

var type = BenchmarkRunner.Find(typeof(EasyEcs.Benchmark.KernelBenchmark).Assembly, args[0]);
int rows = int.Parse(args[1]), calls = int.Parse(args[2]);
string[] names = args.Length > 3 ? args[3].Split(',') : [];
var methods = BenchmarkRunner.Benchmarks(type, names);
// Each case of the class (every value of a non-numeric [Params] / [ParamsSource]: a gate, an enum, a bool), the integer
// ones at the size given, the smallest of other numbers.
foreach (var values in BenchmarkRunner.Combinations(type, v => v.All(x => x is int or long) ? [rows] : BenchmarkRunner.Smallest(v)))
{
    var instance = BenchmarkRunner.Create(type, values);
    foreach (var method in methods)
    {
        var run = method.CreateDelegate<Action>(instance);
        for (int round = 0; round < 2; round++) // the tiering thread compiles in the background: give it time, then run Tier1
        {
            for (int i = 0; i < calls; i++)
            {
                run();
                if (i % 50 == 49) Thread.Sleep(20);
            }
            Thread.Sleep(500);
        }
        Console.WriteLine($"ran {method.Name} {string.Join(", ", values.Select(v => $"{v.Member.Name}={v.Value}"))}");
    }
    BenchmarkRunner.Cleanup(instance);
}
return 0;
