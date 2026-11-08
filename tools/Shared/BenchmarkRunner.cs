using System.Reflection;

/// <summary>
/// Runs BenchmarkDotNet classes without BenchmarkDotNet, for the tools that read what the JIT does with them
/// (tools/perf/Inlining, tools/perf/LoopBound; linked into both): finds a class, sets its <c>[Params]</c> and
/// <c>[ParamsSource]</c> members, calls its <c>[GlobalSetup]</c>, <c>[Benchmark]</c> and <c>[GlobalCleanup]</c> methods.
/// Attributes are matched by name, so the tools need no reference to BenchmarkDotNet's types.
/// </summary>
static class BenchmarkRunner
{
    /// <summary>The benchmark class named <paramref name="name"/>: its name (<c>KernelBenchmark</c>), its name without
    /// the <c>Benchmark</c> suffix (<c>Kernel</c>) or its full name. Throws with the candidates when none or several
    /// match.</summary>
    public static Type Find(Assembly assembly, string name)
    {
        var benchmarks = assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.GetMethods().Any(m => Has(m, "BenchmarkAttribute"))).ToArray();
        var found = benchmarks.Where(t => t.FullName == name).ToArray();
        if (found.Length == 0) found = benchmarks.Where(t => t.Name == name || t.Name == name + "Benchmark").ToArray();
        return found.Length switch
        {
            1 => found[0],
            0 => throw new ArgumentException($"no benchmark class '{name}' (or '{name}Benchmark') in {assembly.GetName().Name}; "
                + $"classes containing it: {string.Join(", ", benchmarks.Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).DefaultIfEmpty("none"))}"),
            _ => throw new ArgumentException($"'{name}' names several benchmark classes, give the full name: {string.Join(", ", found.Select(t => t.FullName))}"),
        };
    }

    /// <summary>The <c>[Benchmark]</c> methods of <paramref name="type"/> without parameters, all or those named.</summary>
    public static MethodInfo[] Benchmarks(Type type, string[] names) =>
        type.GetMethods().Where(m => Has(m, "BenchmarkAttribute") && m.GetParameters().Length == 0 && (names.Length == 0 || names.Contains(m.Name))).ToArray();

    /// <summary>Every combination of the values of the <c>[Params]</c> and <c>[ParamsSource]</c> members (of any type:
    /// numbers, enums, bools, a gate's source), with the values of a member whose values are all numbers replaced by
    /// <paramref name="numeric"/> (the smallest, or a size the caller gives).</summary>
    public static List<(MemberInfo Member, object? Value)>[] Combinations(Type type, Func<object?[], object?[]> numeric)
    {
        List<(MemberInfo, object?)>[] all = [[]];
        foreach (var member in type.GetMembers())
        {
            var attribute = member.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name is "ParamsAttribute" or "ParamsSourceAttribute");
            if (attribute is null) continue;
            var values = attribute.GetType().Name == "ParamsAttribute"
                ? (object?[])attribute.GetType().GetProperty("Values")!.GetValue(attribute)!
                : Source(type, attribute);
            object?[] chosen = values.All(v => v is IConvertible and not string and not Enum and not bool) ? numeric(values) : values;
            all = all.SelectMany(list => chosen.Select(v => new List<(MemberInfo, object?)>(list) { (member, Convert(member, v)) })).ToArray();
        }
        return all;
    }

    /// <summary>The smallest of numeric <c>[Params]</c> values.</summary>
    public static object?[] Smallest(object?[] values) => [values.MinBy(v => System.Convert.ToDouble(v))];

    /// <summary>A new instance of <paramref name="type"/> with <paramref name="values"/> set and its
    /// <c>[GlobalSetup]</c> run.</summary>
    public static object Create(Type type, List<(MemberInfo Member, object? Value)> values)
    {
        var instance = Activator.CreateInstance(type)!;
        foreach (var (member, value) in values)
        {
            if (member is PropertyInfo property) property.SetValue(instance, value);
            else ((FieldInfo)member).SetValue(instance, value);
        }
        Invoke(instance, "GlobalSetupAttribute");
        return instance;
    }

    /// <summary>Runs the <c>[GlobalCleanup]</c> methods of <paramref name="instance"/>.</summary>
    public static void Cleanup(object instance) => Invoke(instance, "GlobalCleanupAttribute");

    public static bool Has(MemberInfo member, string attribute) => member.GetCustomAttributes().Any(a => a.GetType().Name == attribute);

    static void Invoke(object instance, string attribute)
    {
        foreach (var method in instance.GetType().GetMethods().Where(m => Has(m, attribute))) method.Invoke(instance, null);
    }

    // A [ParamsSource]'s values: the static property or method it names, on its own type or the benchmark's.
    static object?[] Source(Type benchmark, Attribute attribute)
    {
        var owner = (Type?)attribute.GetType().GetProperty("Type")?.GetValue(attribute) ?? benchmark;
        var name = (string)attribute.GetType().GetProperty("Name")!.GetValue(attribute)!;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        object? values = owner.GetProperty(name, all)?.GetValue(null) ?? owner.GetMethod(name, all, Type.EmptyTypes)?.Invoke(null, null);
        return ((System.Collections.IEnumerable)values!).Cast<object?>().ToArray();
    }

    // A value converted to the member's type (a size given as an int for a long or a double member).
    static object? Convert(MemberInfo member, object? value)
    {
        var type = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        return value is IConvertible and not string and not Enum and not bool && type != value.GetType() && type.IsPrimitive
            ? System.Convert.ChangeType(value, type)
            : value;
    }
}
