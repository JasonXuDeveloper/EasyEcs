using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The code EasyEcs.Generator writes for a system type: a struct of static members that runs its [ForEach] methods as
/// struct jobs and declares the data they read and write. No instance and no virtual call: the runtime calls
/// <see cref="Update"/> through a function pointer per system and update. Do not implement it by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISystemCode
{
    /// <summary>Runs the type's [ForEach] methods in declaration order (after OnUpdate).</summary>
    static abstract void Update(EcsSystem system);

    /// <summary>Declares what the type's [ForEach] methods read and write (parallel stages).</summary>
    static abstract void Access(ref SystemAccess access);

    /// <summary>
    /// How each [ForEach] method of the type runs, one line per method in declaration order: "Move: vector (Vector512 or
    /// Vector&lt;float&gt;), flat", "Spawn: scalar (it takes the Entity)" (<see cref="SystemInfo.Loops"/>).
    /// </summary>
    static abstract string[] Loops { get; }

    /// <summary>The type's <c>[UpdateInterval(n)]</c>: 0 when it has none (its base's, or every update, applies).</summary>
    static virtual int Interval => 0;

    /// <summary>The phase of the type's <c>[UpdateInterval(n, Phase = p)]</c>, 0 to <see cref="Interval"/> - 1.</summary>
    static virtual int Phase => 0;

    /// <summary>Whether the type or a base type below <see cref="EcsSystem"/> overrides OnUpdate: when none does, the
    /// runtime skips the call.</summary>
    static virtual bool OverridesOnUpdate => false;
}

/// <summary>
/// Names the public code type of a public generic system (an open <see cref="ISystemCode"/> struct over the system's type
/// parameters), so that the generator of an assembly that closes the system registers the closed code itself: no
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
/// The registry of generated system code (<see cref="ISystemCode"/>), filled by a module initializer per system type;
/// <see cref="World.AddSystem"/> collects the code of the system's type and of its base types (base first, across
/// assemblies). Called by generated code only.
/// <br/>
/// The registry holds types weakly: an assembly of systems loaded into a collectible AssemblyLoadContext unloads once
/// its systems are removed from every world, and a newer version of it registers its own types.
/// <br/>
/// A generic system registers its code once per closed type its assembly names (<c>AddSystem&lt;Regen&lt;Health&gt;&gt;()</c>,
/// <c>new Regen&lt;Health&gt;()</c>, <c>class X : Regen&lt;Health&gt;</c>), which needs no runtime code generation, and
/// its code's generic definition for any other closed type, made on first use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class SystemCode
{
    /// <summary>One registered code type: its static members as function pointers and values, read once per AddSystem.</summary>
    internal sealed class Entry
    {
        public delegate*<EcsSystem, void> Update;
        public delegate*<ref SystemAccess, void> Access;
        public string[] Loops = [];
        public int Interval, Phase;
        public bool OverridesOnUpdate;
    }

    private static readonly ConditionalWeakTable<Type, Entry> Registered = new();
    private static readonly ConditionalWeakTable<Type, Type> Generic = new(); // system definition -> open code type

    /// <summary>
    /// Whether <paramref name="world"/> measures its systems (<see cref="WorldOptions.MeasureSystems"/>): the generated
    /// code then counts the entities each [ForEach] pass matches (<see cref="Visit"/>). One field read per pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Measuring(World world) => world.MeasuresSystems;

    /// <summary>Count <paramref name="entities"/> visited by a [ForEach] pass of <paramref name="system"/> (<see cref="SystemStats.LastEntities"/>).</summary>
    public static void Visit(EcsSystem system, int entities) => system.Visited += entities;

    /// <summary>Registers <typeparamref name="TCode"/> as the generated code of <paramref name="system"/> (called by its module initializer).</summary>
    public static void Register<TCode>(Type system) where TCode : struct, ISystemCode => Registered.AddOrUpdate(system, Of<TCode>());

    // Generic over a struct: one instantiation per code type, its static members bound directly (no shared generic code,
    // so the function pointers need no generic context), Update inlined into the trampoline.
    private static Entry Of<TCode>() where TCode : struct, ISystemCode => new()
    {
        Update = &Run<TCode>,
        Access = &Declare<TCode>,
        Loops = TCode.Loops,
        Interval = TCode.Interval,
        Phase = TCode.Phase,
        OverridesOnUpdate = TCode.OverridesOnUpdate,
    };

    private static void Run<TCode>(EcsSystem system) where TCode : struct, ISystemCode => TCode.Update(system);

    private static void Declare<TCode>(ref SystemAccess access) where TCode : struct, ISystemCode => TCode.Access(ref access);

    /// <summary>
    /// Registers the generated code of generic system <paramref name="definition"/>: <paramref name="code"/> is the
    /// code's type, open over the system's type parameters (called by the system's module initializer).
    /// </summary>
    public static void RegisterGeneric(Type definition, Type code) => Generic.AddOrUpdate(definition, code);

    /// <summary>
    /// The code of <paramref name="type"/> and its base types, base first; <paramref name="own"/> is whether
    /// <paramref name="type"/> itself has generated code (a type without may override OnUpdate unseen). Cold: once per
    /// AddSystem.
    /// </summary>
    internal static Entry[] For(Type type, out bool own)
    {
        var chain = new List<Entry>();
        own = TryGet(type, out _);
        for (var t = type; t != null && t != typeof(EcsSystem); t = t.BaseType)
            if (TryGet(t, out var code)) chain.Add(code);

        chain.Reverse();
        return chain.ToArray();
    }

    private static bool TryGet(Type type, out Entry code)
    {
        if (Registered.TryGetValue(type, out code!)) return true;
        if (!type.IsConstructedGenericType || !Generic.TryGetValue(type.GetGenericTypeDefinition(), out var open)) return false;
        code = Close(open, type);
        Registered.AddOrUpdate(type, code);
        return true;
    }

    private static readonly MethodInfo OfDefinition = typeof(SystemCode).GetMethod(nameof(Of), BindingFlags.NonPublic | BindingFlags.Static)!;

    // A closed type the system's assembly does not name (another assembly closes it): the code is made at run time.
    // Native AOT then needs the instantiation kept, which naming the closed type in the system's assembly does.
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Closed types named in the system's assembly are registered without it")]
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The code type is a struct of static members the generated code names")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Of<TCode> only reads static members the generated code names")]
    [UnconditionalSuppressMessage("Trimming", "IL2071", Justification = "The code type is a struct of static members the generated code names")]
    private static Entry Close(Type open, Type system)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException($"No generated code is registered for {system}: under Native AOT, name the closed type in a project that references EasyEcs.Generator (AddSystem<{system.Name}>(), new, typeof or a base type), and it registers the code itself.");
        return (Entry)OfDefinition.MakeGenericMethod(open.MakeGenericType(system.GetGenericArguments())).Invoke(null, null)!;
    }
}
