using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using EasyEcs.Benchmark.Locality;

namespace EasyEcs.Benchmark;

/// <summary>
/// An ISA column in the summary table: <see cref="Isa.Name"/> of the process that ran BenchmarkDotNet, which passes its
/// environment (<c>DOTNET_EnableAVX=0</c> included) to the processes it measures; their setup prints their own line.
/// </summary>
public sealed class IsaColumnAttribute() : ColumnConfigBaseAttribute(new IsaColumn());

public sealed class IsaColumn : IColumn
{
    public string Id => "ISA";
    public string ColumnName => "ISA";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Job;
    public int PriorityInCategory => 0;
    public bool IsNumeric => false;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "The vector instructions the kernels ran on";
    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => Isa.Name;
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) => Isa.Name;
}
