using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EasyEcs;

/// <summary>
/// The code EasyEcs.Generator writes for one system type: a struct of static members that runs its hooks and its
/// [ForEach] methods as struct jobs, and declares the data they read and write. No instance and no virtual call: the
/// system set of an assembly (<see cref="ISystemSet"/>) calls it directly. Do not implement it by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISystemCode
{
    /// <summary>Runs the system: its OnUpdate method, then its [ForEach] methods in declaration order.</summary>
    static abstract void Update(World world, SystemHandle system);

    /// <summary>Runs its OnCreate method, if it has one.</summary>
    static abstract void Create(World world, SystemHandle system);

    /// <summary>Runs its OnDestroy method, if it has one.</summary>
    static abstract void Destroy(World world, SystemHandle system);

    /// <summary>Declares what the system reads and writes (parallel stages).</summary>
    static abstract void Access(ref SystemAccess access);

    /// <summary>
    /// How each [ForEach] method of the type runs, one line per method in declaration order: "Move: vector (Vector512 or
    /// Vector&lt;float&gt;), flat", "Spawn: scalar (it takes the Entity)" (<see cref="SystemInfo.Loops"/>).
    /// </summary>
    static abstract string[] Loops { get; }

    /// <summary>The type's <c>[UpdateInterval(n)]</c>: 1 (every update) without one.</summary>
    static virtual int Interval => 1;

    /// <summary>The phase of the type's <c>[UpdateInterval(n, Phase = p)]</c>, 0 to <see cref="Interval"/> - 1.</summary>
    static virtual int Phase => 0;
}

/// <summary>
/// The systems of one assembly, written by EasyEcs.Generator (<c>__EasyEcsSystems</c>): every system the assembly
/// declares and every closed generic system it names, numbered from 0, and a switch per hook over those numbers whose
/// cases call each system's code (<see cref="ISystemCode"/>) directly. The world runs consecutive systems of one set
/// with one call (<see cref="World.RunSystems{TSet}"/>), so systems of one assembly call each other's code directly.
/// Registered by a module initializer (<see cref="SystemCode.Register{TSet}"/>). Do not implement it by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISystemSet
{
    /// <summary>Declares the set's systems, in the order of their numbers.</summary>
    static abstract void Declare(ref SystemDeclarations systems);

    /// <summary>Runs system <paramref name="system"/> of the set (<see cref="ISystemCode.Update"/>).</summary>
    static abstract void Update(World world, int system, SystemHandle handle);

    /// <summary>Runs the OnCreate method of system <paramref name="system"/> of the set.</summary>
    static abstract void Create(World world, int system, SystemHandle handle);

    /// <summary>Runs the OnDestroy method of system <paramref name="system"/> of the set.</summary>
    static abstract void Destroy(World world, int system, SystemHandle handle);

    /// <summary>
    /// Runs the two systems of fusion <paramref name="fusion"/> (<see cref="SystemDeclarations.Fuse"/>) as one walk (pass
    /// fusion): <paramref name="first"/>'s [ForEach] methods, then <paramref name="second"/>'s, on each block of rows,
    /// the second's writes stamped with <paramref name="secondTick"/>. False when it ran nothing: a walk of fewer rows
    /// than <see cref="SystemCode.FusedRows"/> (each system then runs its own code), or a set without fusions.
    /// </summary>
    static virtual bool RunFused(World world, int fusion, SystemHandle first, SystemHandle second, ulong secondTick) => false;
}

/// <summary>The systems an <see cref="ISystemSet"/> declares (<see cref="ISystemSet.Declare"/>). Called by generated code only.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct SystemDeclarations
{
    internal SystemCode.Entry[] Entries;
    internal int Count;
    internal SystemCode.Set Set;

    internal (int First, int Second)[] Fusions;

    /// <summary>
    /// Declares that systems <paramref name="first"/> and <paramref name="second"/> of the set (their numbers) run as one
    /// walk when the schedule puts the second right after the first: fusion number n for the n-th call
    /// (<see cref="ISystemSet.RunFused"/>).
    /// </summary>
    public void Fuse(int first, int second) => Fusions = [.. Fusions ?? [], (first, second)];

    /// <summary>Declares system <typeparamref name="TSystem"/>, run by <typeparamref name="TCode"/>: the next number of the set.</summary>
    public void Add<TSystem, TCode>() where TSystem : struct, allows ref struct where TCode : struct, ISystemCode
    {
        if (Count == Entries.Length) Array.Resize(ref Entries, Math.Max(4, Count * 2));
        Entries[Count] = SystemCode.Entry.Of<TCode>(typeof(TSystem), Set, Count);
        Count++;
    }
}

/// <summary>
/// Names the public code type of a public generic system (an open <see cref="ISystemCode"/> struct over the system's type
/// parameters), so that the generator of an assembly that closes the system puts the closed code into its own set: no
/// runtime code generation, as Native AOT and WebAssembly need. Written by the generator, read by it in referencing
/// assemblies.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true), EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GenericSystemCodeAttribute(Type system, Type code) : Attribute
{
    /// <summary>The generic system's definition (<c>typeof(Regen&lt;&gt;)</c>).</summary>
    public Type System { get; } = system;

    /// <summary>Its code's definition, open over the same type parameters.</summary>
    public Type Code { get; } = code;
}

/// <summary>
/// The registry of the generated system sets (<see cref="ISystemSet"/>), filled by one module initializer per assembly,
/// and the helpers generated system code calls. <see cref="World.AddSystem{T}"/> finds a system's code here by its type.
/// Called by generated code only.
/// <br/>
/// The registry holds systems weakly: an assembly of systems loaded into a collectible AssemblyLoadContext unloads once
/// its systems are removed from every world, and a newer version of it registers its own. A closed generic system that
/// several assemblies name is registered by the first of them to load.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class SystemCode
{
    /// <summary>
    /// One registered set: the function pointers of its switches, bound once (<see cref="World.RunSystems{TSet}"/> and
    /// the hooks), and its systems by number.
    /// </summary>
    internal sealed class Set
    {
        public delegate*<World, ReadOnlySpan<World.SystemSlot>, long, ReadOnlySpan<bool>, ReadOnlySpan<short>, void> Run;
        public delegate*<World, int, SystemHandle, void> Create, Destroy;
        public Entry[] Systems = [];

        /// <summary>The pairs of systems (by number) that run as one walk when next to each other (<see cref="ISystemSet.RunFused"/>).</summary>
        public (int First, int Second)[] Fusions = [];

        /// <summary>The fusion of systems <paramref name="first"/> then <paramref name="second"/>, or -1. Cold: once per schedule.</summary>
        public int FusionOf(int first, int second) => Array.IndexOf(Fusions, (first, second));
    }

    /// <summary>One system of a set: its type, its set and number, and its code's values, read once per AddSystem.</summary>
    internal sealed class Entry
    {
        public Type Type;
        public Set Set;
        public int Id;
        public string[] Loops = [];
        public int Interval = 1, Phase;
        private delegate*<ref SystemAccess, void> _access;

        // Read from the type's attributes on first use (reflection, cold): its group and orderings.
        private bool _scheduled;
        public int Group;
        public string GroupName;
        public Type[] Before = [], After = [];

        // Built on first use: component bits are taken as their types are first touched.
        private SystemAccess _declared;
        private bool _hasAccess;
        private readonly Lock _lock = new(); // cold: the first AddSystem of the type, in whichever world

        public static Entry Of<TCode>(Type type, Set set, int id) where TCode : struct, ISystemCode => new()
        {
            Type = type,
            Set = set,
            Id = id,
            Loops = TCode.Loops,
            Interval = TCode.Interval,
            Phase = TCode.Phase,
            _access = &Declare<TCode>,
        };

        private static void Declare<TCode>(ref SystemAccess access) where TCode : struct, ISystemCode => TCode.Access(ref access);

        /// <summary>What the system reads and writes (declared once per process).</summary>
        public ref readonly SystemAccess Access
        {
            get
            {
                if (!Volatile.Read(ref _hasAccess))
                    lock (_lock)
                        if (!_hasAccess)
                        {
                            var access = default(SystemAccess);
                            _access(ref access);
                            _declared = access;
                            Volatile.Write(ref _hasAccess, true);
                        }

                return ref _declared;
            }
        }

        /// <summary>Reads the group and orderings from the type's attributes once.</summary>
        public void Schedule()
        {
            if (Volatile.Read(ref _scheduled)) return;
            lock (_lock)
            {
                if (_scheduled) return;
                (Group, GroupName, Before, After) = World.GroupOf(Type);
                Volatile.Write(ref _scheduled, true);
            }
        }
    }

    // System type -> its entry, weakly both ways: a collectible type's key goes with its context, and an entry of a
    // collectible set registered for a type of another context does not root the set (its own assembly does, through
    // Sets<TSet>.Instance).
    private static readonly ConditionalWeakTable<Type, WeakReference<Entry>> Registered = new();
    private static readonly Lock RegisterLock = new(); // cold: module initializers and the first AddSystem of a type

    private static class Sets<TSet> where TSet : struct, ISystemSet
    {
        public static Set Instance; // roots the set for as long as its assembly lives
    }

    /// <summary>
    /// Registers the system set <typeparamref name="TSet"/> (called by its assembly's module initializer): its systems,
    /// and the function pointers of its switches.
    /// </summary>
    public static void Register<TSet>() where TSet : struct, ISystemSet
    {
        var set = new Set
        {
            Run = &World.RunSystems<TSet>,
            Create = &Create<TSet>,
            Destroy = &Destroy<TSet>,
        };
        var declarations = new SystemDeclarations { Entries = [], Set = set };
        TSet.Declare(ref declarations);
        set.Systems = declarations.Entries.AsSpan(0, declarations.Count).ToArray();
        set.Fusions = declarations.Fusions ?? [];
        Sets<TSet>.Instance = set;
        lock (RegisterLock)
            foreach (var entry in set.Systems)
                if (!Registered.TryGetValue(entry.Type, out var known) || !known.TryGetTarget(out _))
                    Registered.AddOrUpdate(entry.Type, new WeakReference<Entry>(entry));
    }

    private static void Create<TSet>(World world, int system, SystemHandle handle) where TSet : struct, ISystemSet => TSet.Create(world, system, handle);

    private static void Destroy<TSet>(World world, int system, SystemHandle handle) where TSet : struct, ISystemSet => TSet.Destroy(world, system, handle);

    /// <summary>
    /// The entry of system type <paramref name="type"/>: registered by a set, or by the set of the type's own assembly
    /// once its module initializer runs (a library whose code nothing has called yet). Cold: once per AddSystem.
    /// </summary>
    internal static Entry Of(Type type)
    {
        if (Find(type) is { } entry) return entry;
        RuntimeHelpers.RunModuleConstructor(type.Module.ModuleHandle);
        if (Find(type) is { } loaded) return loaded;
        throw new NotSupportedException(type.IsConstructedGenericType
            ? $"No generated code is registered for {ScheduleInfo.NameOf(type)}: name the closed type in a project that references EasyEcs.Generator (AddSystem<{ScheduleInfo.NameOf(type)}>(), HasSystem, System, TryGetSystem, RemoveSystem or [UpdateBefore] / [UpdateAfter]), and its system set runs it. A closed type made at run time (by reflection) has no code."
            : $"{ScheduleInfo.NameOf(type)} is no system: declare it [EcsSystem] ref struct in a project that references EasyEcs.Generator (EECS077)");
    }

    private static Entry Find(Type type)
    {
        lock (RegisterLock)
            return Registered.TryGetValue(type, out var weak) && weak.TryGetTarget(out var entry) ? entry : null;
    }

    /// <summary>
    /// Whether <paramref name="world"/> measures its systems (<see cref="WorldOptions.MeasureSystems"/>): the generated
    /// code then counts the entities each [ForEach] pass matches (<see cref="Visit"/>). One field read per pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Measuring(World world) => world.MeasuresSystems;

    /// <summary>Count <paramref name="entities"/> visited by a [ForEach] pass of <paramref name="system"/> (<see cref="SystemStats.LastEntities"/>).</summary>
    public static void Visit(SystemHandle system, int entities) => system.Row.Visited += entities;

    private const string Blamed = "EasyEcs.FusedSystem";

    /// <summary>
    /// The rows from which passes of vector or batch loops run as one walk (pass fusion, per block of 256 rows); a walk
    /// of fewer rows runs each pass on its own. Below it the rows of both passes stay in the L2 cache between them, and
    /// the blocks only add their bookkeeping (FusionBenchmark: 1.04-1.07 of the passes apart at 10,000 rows, 0.98-1.03 at
    /// 100,000, 0.55-0.64 from 1,000,000). Per-row walks (scalar methods) and parallel walks fuse at any size.
    /// </summary>
    public const int FusedRows = 1 << 17;

    /// <summary>
    /// The exception filter of a walk that runs two systems (<see cref="ISystemSet.RunFused"/>): notes that system
    /// <paramref name="system"/> (0 the first, 1 the second) threw <paramref name="error"/>, on the thread that threw,
    /// and lets it go on unchanged (false). The world disables that system (<see cref="SystemErrorPolicy.Disable"/>).
    /// </summary>
    public static bool Blame(Exception error, int system)
    {
        if (!error.Data.Contains(Blamed)) error.Data[Blamed] = system;
        return false;
    }

    /// <summary>The system of a two-system walk that threw <paramref name="error"/> (<see cref="Blame"/>): 0 or 1.</summary>
    internal static int Culprit(Exception error) => error.Data[Blamed] is int system ? system : 0;

    /// <summary>
    /// Stamps rows [<paramref name="start"/>, <paramref name="start"/> + <paramref name="count"/>) of the run
    /// <paramref name="entities"/> as written at <paramref name="tick"/> (a tracked <typeparamref name="T"/>): the writes of
    /// the second system of a two-system walk, made in the stage after the walk's own.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StampRows<T>(in EntitySpan entities, int start, int count, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked) entities.Table.StampRows<T>(entities.First + start, count, tick);
    }

    /// <summary>
    /// <see cref="StampRows{T}"/> for the rows of a word of which some pass: row i of <paramref name="entities"/> when bit
    /// i of <paramref name="rows"/> is set (the run lies in one word of 64 rows).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StampMasked<T>(in EntitySpan entities, ulong rows, ulong tick) where T : struct, IComponent
    {
        if (default(T) is ITracked) entities.Table.StampWord<T>(entities.First >> 6, rows << (entities.First & 63), tick);
    }
}
