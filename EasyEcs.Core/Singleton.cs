using System.Threading;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

/// <summary>
/// Process-wide dense index per singleton component type; the value lives per <see cref="Context"/>.
/// </summary>
internal static class SingletonIndex
{
    internal static int Count;
}

internal static class SingletonIndex<T> where T : struct, ISingletonComponent
{
    internal static readonly int Value = Interlocked.Increment(ref SingletonIndex.Count) - 1;
}

/// <summary>
/// Heap box holding one context's singleton value; refs into it stay valid for the box lifetime.
/// </summary>
internal sealed class SingletonBox<T> where T : struct, ISingletonComponent
{
    public T Value;
}
