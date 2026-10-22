using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The code EasyEcs.Generator writes for a system type: its [ForEach] methods as struct jobs and the data they read and
/// write. A module initializer of the system's assembly registers one instance per type; <see cref="World.AddSystem"/>
/// collects the code of the system's type and of its base types (base first, across assemblies). Do not derive from
/// it by hand.
/// <br/>
/// The registry holds types weakly: an assembly of systems loaded into a collectible AssemblyLoadContext unloads once
/// its systems are removed from every world, and a newer version of it registers its own types.
/// <br/>
/// A generic system registers its code once per closed type its assembly names (<c>AddSystem&lt;Regen&lt;Health&gt;&gt;()</c>,
/// <c>new Regen&lt;Health&gt;()</c>, <c>class X : Regen&lt;Health&gt;</c>), which needs no runtime code generation, and
/// its code's generic definition for any other closed type, made on first use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class SystemCode
{
    private static readonly ConditionalWeakTable<Type, SystemCode> Registered = new();

    /// <summary>The code of a type without <see cref="UpdateIntervalAttribute">[UpdateInterval]</see>.</summary>
    protected SystemCode()
    {
    }

    /// <summary>
    /// The code of a type with <c>[UpdateInterval(<paramref name="interval"/>, Phase = <paramref name="phase"/>)]</c>,
    /// as the generator writes it (interval 1 or more, phase 0 to interval - 1).
    /// </summary>
    protected SystemCode(int interval, int phase)
    {
        Interval = interval;
        Phase = phase;
    }

    // The type's [UpdateInterval] (0: it has none, its base's or every update applies).
    internal readonly int Interval, Phase;
    private static readonly ConditionalWeakTable<Type, Type> Generic = new(); // system definition -> open code type

    /// <summary>Runs the type's [ForEach] methods in declaration order (after OnUpdate).</summary>
    protected abstract void Update(EcsSystem system);

    /// <summary>Declares what the type's [ForEach] methods read and write (parallel stages).</summary>
    protected abstract void Access(ref SystemAccess access);

    /// <summary>
    /// Whether <paramref name="world"/> measures its systems (<see cref="WorldOptions.MeasureSystems"/>): the generated
    /// code then counts the entities each [ForEach] pass matches (<see cref="Visit"/>). One field read per pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static bool Measuring(World world) => world.MeasuresSystems;

    /// <summary>Count <paramref name="entities"/> visited by a [ForEach] pass of <paramref name="system"/> (<see cref="SystemStats.LastEntities"/>).</summary>
    protected static void Visit(EcsSystem system, int entities) => system.Visited += entities;

    /// <summary>
    /// How each [ForEach] method of the type runs, one line per method in declaration order: "Move: vector (Vector512 or
    /// Vector&lt;float&gt;), flat", "Spawn: scalar (it takes the Entity)" (<see cref="SystemInfo.Loops"/>).
    /// </summary>
    protected virtual string[] Loops => [];

    // Protected, not protected internal: generated overrides are then the same in every assembly, friends included.
    internal void Run(EcsSystem system) => Update(system);

    internal string[] Describe() => Loops;

    internal void Declare(ref SystemAccess access) => Access(ref access);

    /// <summary>Registers the generated code of <paramref name="system"/> (called by its module initializer).</summary>
    public static void Register(Type system, SystemCode code) => Registered.AddOrUpdate(system, code);

    /// <summary>
    /// Registers the generated code of generic system <paramref name="definition"/>: <paramref name="code"/> is the
    /// code's type, open over the system's type parameters (called by the system's module initializer).
    /// </summary>
    public static void RegisterGeneric(Type definition,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicConstructors)] Type code) =>
        Generic.AddOrUpdate(definition, code);

    /// <summary>
    /// The code of <paramref name="type"/> and its base types, base first; <paramref name="own"/> is whether
    /// <paramref name="type"/> itself has generated code (a type without may override OnUpdate unseen). Cold: once per
    /// AddSystem.
    /// </summary>
    internal static SystemCode[] For(Type type, out bool own)
    {
        var chain = new List<SystemCode>();
        own = TryGet(type, out _);
        for (var t = type; t != null && t != typeof(EcsSystem); t = t.BaseType)
            if (TryGet(t, out var code)) chain.Add(code);

        chain.Reverse();
        return chain.ToArray();
    }

    private static bool TryGet(Type type, out SystemCode code)
    {
        if (Registered.TryGetValue(type, out code)) return true;
        if (!type.IsConstructedGenericType || !Generic.TryGetValue(type.GetGenericTypeDefinition(), out var open)) return false;
        code = Close(open, type);
        Registered.AddOrUpdate(type, code);
        return true;
    }

    // A closed type the system's assembly does not name (another assembly closes it): the code is made at run time.
    // Native AOT then needs the instantiation kept, which naming the closed type in the system's assembly does.
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Closed types named in the system's assembly are registered without it")]
    [UnconditionalSuppressMessage("Trimming", "IL2055", Justification = "The code type's constructors are kept by RegisterGeneric")]
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "The code type's constructors are kept by RegisterGeneric")]
    private static SystemCode Close(Type open, Type system) =>
        (SystemCode)Activator.CreateInstance(open.MakeGenericType(system.GetGenericArguments()), nonPublic: true)!;
}
