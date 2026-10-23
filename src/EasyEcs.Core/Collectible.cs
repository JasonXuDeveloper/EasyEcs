using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace EasyEcs;

/// <summary>
/// Types from collectible <see cref="AssemblyLoadContext"/>s (game logic loaded as a plugin, reloaded while the
/// process runs). When such a context unloads, EasyEcs forgets its types: their component bits are reused once no live
/// world mentions them, and saves stop naming them. Worlds drop them first with <see cref="World.RemoveTypes(Assembly)"/>.
/// Nothing here roots a context: contexts and worlds are held weakly.
/// </summary>
internal static class Collectible
{
    private static readonly object Marker = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, object> Watched = new();
    private static readonly ConditionalWeakTable<World, object> Worlds = new(); // every world not disposed

    /// <summary>Forget the types of <paramref name="type"/>'s contexts when they unload (a no-op for the default one).</summary>
    public static void Watch(Type type)
    {
        foreach (var assembly in AssembliesOf(type))
            if (AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true } context && Watched.TryAdd(context, Marker))
                context.Unloading += Unloading; // a static method: the context holds nothing of ours but the delegate
    }

    private static void Unloading(AssemblyLoadContext context)
    {
        bool From(Type type) => Uses(type, assembly => AssemblyLoadContext.GetLoadContext(assembly) == context);
        TypeRegistry.Forget(From);
        ComponentInfo.Release(From);
    }

    /// <summary>Whether <paramref name="type"/>, or a type it is built from (generic arguments, elements), is in an assembly <paramref name="of"/> matches.</summary>
    public static bool Uses(Type type, Func<Assembly, bool> of)
    {
        foreach (var assembly in AssembliesOf(type))
            if (of(assembly))
                return true;
        return false;
    }

    private static System.Collections.Generic.IEnumerable<Assembly> AssembliesOf(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var assembly in AssembliesOf(type.GetElementType()!)) yield return assembly;
            yield break;
        }

        yield return type.Assembly;
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
        foreach (var assembly in AssembliesOf(argument))
            yield return assembly;
    }

    public static void Track(World world) => Worlds.AddOrUpdate(world, Marker);

    public static void Untrack(World world) => Worlds.Remove(world);

    /// <summary>Whether a live world mentions component <paramref name="bit"/> (a table, a cached query, a grouping or an index).</summary>
    public static bool InUse(int bit)
    {
        foreach (var pair in Worlds)
            if (pair.Key.Mentions(bit))
                return true;
        return false;
    }
}
