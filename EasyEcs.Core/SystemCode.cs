using System;
using System.Collections.Generic;
using System.ComponentModel;
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
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class SystemCode
{
    private static readonly ConditionalWeakTable<Type, SystemCode> Registered = new();

    /// <summary>Runs the type's [ForEach] methods in declaration order (after OnUpdate).</summary>
    protected abstract void Update(EcsSystem system);

    /// <summary>Declares what the type's [ForEach] methods read and write (parallel stages).</summary>
    protected abstract void Access(ref SystemAccess access);

    // Protected, not protected internal: generated overrides are then the same in every assembly, friends included.
    internal void Run(EcsSystem system) => Update(system);

    internal void Declare(ref SystemAccess access) => Access(ref access);

    /// <summary>Registers the generated code of <paramref name="system"/> (called by its module initializer).</summary>
    public static void Register(Type system, SystemCode code) => Registered.AddOrUpdate(system, code);

    /// <summary>
    /// The code of <paramref name="type"/> and its base types, base first; <paramref name="own"/> is whether
    /// <paramref name="type"/> itself has generated code (a type without may override OnUpdate unseen). Cold: once per
    /// AddSystem.
    /// </summary>
    internal static SystemCode[] For(Type type, out bool own)
    {
        var chain = new List<SystemCode>();
        own = Registered.TryGetValue(type, out _);
        for (var t = type; t != null && t != typeof(EcsSystem); t = t.BaseType)
            if (Registered.TryGetValue(t, out var code)) chain.Add(code);

        chain.Reverse();
        return chain.ToArray();
    }
}
