using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;

// Runs each [Benchmark] method of the classes named (all of the default set; KernelBenchmark or Kernel) once, with
// every value of its non-numeric [Params] / [ParamsSource] and the smallest numeric ones (tools/Shared/BenchmarkRunner.cs), while an EventListener reads the JIT's inlining decisions (JitTracing keyword
// 0x1000). Run without tiered compilation (tools/perf/inlining.sh sets DOTNET_TieredCompilation=0): every method is compiled
// optimized at its first call, with the inliner's decisions of Tier1, so one call of each runner decides what it
// inlines, masked words and the last rows of a run included. Prints each job member (a type of a generated __ForEach
// class) a method failed to inline and why; exit code 1 if there is one.
string[] classes = args.Length > 0 ? args : ["KernelBenchmark", "WorldSimulationBenchmark", "BattleServerBenchmark", "MultiWorldBenchmark",
    "FusionBenchmark", "LookupGatherBenchmark"];
using var listener = new Inlining();
var assembly = typeof(EasyEcs.Benchmark.KernelBenchmark).Assembly;
foreach (var name in classes)
{
    var type = BenchmarkRunner.Find(assembly, name);
    var benchmarks = BenchmarkRunner.Benchmarks(type, []);
    foreach (var values in BenchmarkRunner.Combinations(type, BenchmarkRunner.Smallest))
    {
        var instance = BenchmarkRunner.Create(type, values);
        foreach (var benchmark in benchmarks) benchmark.Invoke(instance, null);
        BenchmarkRunner.Cleanup(instance);
    }
    Console.WriteLine($"{type.Name}: {benchmarks.Length} benchmarks run");
}
Thread.Sleep(2000); // the listener receives the runtime's events on a thread of its own
var failed = listener.Failed.Keys.Order().ToArray();
foreach (var line in failed) Console.WriteLine(line);
Console.WriteLine(failed.Length == 0 ? "every generated job inlined" : $"{failed.Length} failed inlines of generated jobs");
return failed.Length == 0 ? 0 : 1;

/// <summary>The inlining failures whose inlinee is a member of a generated job.</summary>
sealed class Inlining : EventListener
{
    public readonly ConcurrentDictionary<string, bool> Failed = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1000);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName != "MethodJitInliningFailed" || e.PayloadNames is null || e.Payload is null) return;
        string Get(string n) => e.PayloadNames.IndexOf(n) is >= 0 and var i ? e.Payload[i]?.ToString() ?? "" : "";
        string owner = Get("InlineeNamespace"), name = Get("InlineeName");
        // A job's Execute or loop method (a type of a generated __ForEach class, not a runner type over one, nor the
        // user's method __ForEach.Name_k, which may be large).
        if (!owner.Contains("__ForEach+") || owner.EndsWith(']') || name != "Execute" && !name.StartsWith("__", StringComparison.Ordinal)
            || Get("InlinerName") == ".cctor") return; // a static constructor's checks run once (SteerJob18.__Layout1)
        // A cold loop marked NoInlining is meant to be called.
        var named = typeof(EasyEcs.Benchmark.KernelBenchmark).Assembly.GetType(owner)
            ?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == name).ToArray();
        if (named is { Length: > 0 } && named.All(m => m.MethodImplementationFlags.HasFlag(MethodImplAttributes.NoInlining))) return;
        string job = owner[(owner.LastIndexOf('+') + 1)..], signature = Get("InlineeNameSignature");
        Failed.TryAdd($"{Get("InlinerName")} <- {job}::{name}{signature[signature.IndexOf('(')..]}: {Get("FailReason")}", true);
    }
}
