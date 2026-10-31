using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace EasyEcs.Benchmark;

/// <summary>Adds <see cref="PageFaultDiagnoser"/> to a class: the kernel's side of each op in the summary table.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PageFaultDiagnoserAttribute : Attribute, IConfigSource
{
    public IConfig Config { get; } = ManualConfig.CreateEmpty().AddDiagnoser(new PageFaultDiagnoser());
}

/// <summary>
/// Page faults and the kernel time an op costs (Linux), read by the host around the actual iterations of one extra
/// launch: the measured process's <c>minflt</c>, <c>majflt</c> and <c>stime</c> (<c>/proc/&lt;pid&gt;/stat</c>) and the
/// machine's <c>thp_fault_alloc</c> (a huge page faulted), <c>thp_fault_fallback</c> (a huge page wanted, 4 KiB pages
/// given), <c>compact_stall</c> (a fault that waited for compaction) and <c>thp_collapse_alloc</c> (khugepaged collapsed
/// a range) from <c>/proc/vmstat</c>, each divided by the ops measured. The vmstat counters are machine-wide: read them
/// under the benchmark lock, with nothing else running. Elsewhere the columns stay empty.
/// </summary>
public sealed class PageFaultDiagnoser : IDiagnoser
{
    private static readonly string[] VmStats = ["thp_fault_alloc", "thp_fault_fallback", "compact_stall", "thp_collapse_alloc"];

    private static readonly Descriptor[] Descriptors =
    [
        new("MinFlt", "minflt/op", "Minor page faults per op (/proc/self/stat)", 0),
        new("MajFlt", "majflt/op", "Major page faults per op (/proc/self/stat)", 1),
        new("STime", "stime ms/op", "Kernel time per op in ms (/proc/self/stat stime)", 2),
        new("ThpAlloc", "thp_alloc/op", "Huge pages faulted per op (/proc/vmstat thp_fault_alloc)", 3),
        new("ThpFallback", "thp_fallback/op", "Huge-page faults given 4 KiB pages per op (/proc/vmstat thp_fault_fallback)", 4),
        new("CompactStall", "compact_stall/op", "Faults that waited for compaction per op (/proc/vmstat compact_stall)", 5),
        new("ThpCollapse", "thp_collapse/op", "Ranges collapsed by khugepaged per op (/proc/vmstat thp_collapse_alloc)", 6),
    ];

    private readonly Dictionary<BenchmarkCase, double[]> _before = new(), _delta = new();

    public IEnumerable<string> Ids => ["PageFaults"];
    public IEnumerable<IExporter> Exporters => [];
    public IEnumerable<IAnalyser> Analysers => [];

    public RunMode GetRunMode(BenchmarkCase benchmarkCase) => OperatingSystem.IsLinux() ? RunMode.ExtraRun : RunMode.None;

    public bool RequiresBlockingAcknowledgments(BenchmarkCase benchmarkCase) => true;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        if (signal == HostSignal.BeforeActualRun) _before[parameters.BenchmarkCase] = Read(parameters.Process.Id);
        else if (signal == HostSignal.AfterActualRun && _before.TryGetValue(parameters.BenchmarkCase, out var before))
        {
            var after = Read(parameters.Process.Id);
            for (int i = 0; i < after.Length; i++) after[i] -= before[i];
            _delta[parameters.BenchmarkCase] = after;
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results)
    {
        if (!_delta.TryGetValue(results.BenchmarkCase, out var delta) || results.TotalOperations == 0) yield break;
        for (int i = 0; i < Descriptors.Length; i++) yield return new Metric(Descriptors[i], delta[i] / results.TotalOperations);
    }

    public void DisplayResults(ILogger logger) { }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => [];

    /// <summary>minflt, majflt, stime (ms) of <paramref name="pid"/>, then the vmstat counters.</summary>
    private static double[] Read(int pid)
    {
        var values = new double[3 + VmStats.Length];
        try
        {
            // Fields after the command's closing parenthesis: state is field 3, minflt 10, majflt 12, stime 15.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            values[0] = double.Parse(fields[10 - 3], CultureInfo.InvariantCulture);
            values[1] = double.Parse(fields[12 - 3], CultureInfo.InvariantCulture);
            values[2] = double.Parse(fields[15 - 3], CultureInfo.InvariantCulture) * 1000.0 / 100; // USER_HZ = 100
            foreach (var line in File.ReadLines("/proc/vmstat"))
            {
                int space = line.IndexOf(' ');
                int at = Array.IndexOf(VmStats, line[..space]);
                if (at >= 0) values[3 + at] = double.Parse(line.AsSpan(space + 1), CultureInfo.InvariantCulture);
            }
        }
        catch (Exception) // not Linux, or the process is gone: the columns stay empty
        {
        }

        return values;
    }

    private sealed class Descriptor(string id, string name, string legend, int priority) : IMetricDescriptor
    {
        public string Id => id;
        public string DisplayName => name;
        public string Legend => legend;
        public string NumberFormat => "N1";
        public UnitType UnitType => UnitType.Dimensionless;
        public string Unit => "";
        public bool TheGreaterTheBetter => false;
        public int PriorityInCategory => priority;
        public bool GetIsAvailable(Metric metric) => true;
    }
}
