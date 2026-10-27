using System.Reflection;

// Runs the [Benchmark] methods of one benchmark class (all, or those named) often enough for tiered compilation to
// promote their runners to Tier1, with the class's int [Params] set to the size given. tools/loopbound.sh runs it with
// DOTNET_JitDisasm and reads the loops of the Tier1 listings.
//
//   LoopBound <class> <rows> <calls> [method,method,...]
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: LoopBound <benchmark class> <rows> <calls> [methods, comma separated]");
    return 2;
}

var type = typeof(EasyEcs.Benchmark.KernelBenchmark).Assembly.GetTypes().Single(t => t.Name == args[0]);
int rows = int.Parse(args[1]), calls = int.Parse(args[2]);
string[] names = args.Length > 3 ? args[3].Split(',') : [];
var instance = Activator.CreateInstance(type)!;
foreach (var member in type.GetMembers().Where(m => Has(m, "ParamsAttribute")))
{
    if (member is FieldInfo { FieldType: var f } field && f == typeof(int)) field.SetValue(instance, rows);
    else if (member is PropertyInfo { PropertyType: var p } property && p == typeof(int)) property.SetValue(instance, rows);
}
foreach (var setup in type.GetMethods().Where(m => Has(m, "GlobalSetupAttribute"))) setup.Invoke(instance, null);
foreach (var method in type.GetMethods().Where(m => Has(m, "BenchmarkAttribute") && m.GetParameters().Length == 0 && (names.Length == 0 || names.Contains(m.Name))))
{
    var run = method.CreateDelegate<Action>(instance);
    for (int i = 0; i < calls; i++)
    {
        run();
        if (i % 50 == 49) Thread.Sleep(20); // the tiering thread compiles in the background
    }
    Console.WriteLine($"ran {method.Name}");
}
foreach (var cleanup in type.GetMethods().Where(m => Has(m, "GlobalCleanupAttribute"))) cleanup.Invoke(instance, null);
return 0;

static bool Has(MemberInfo member, string attribute) => member.GetCustomAttributes().Any(a => a.GetType().Name == attribute);
