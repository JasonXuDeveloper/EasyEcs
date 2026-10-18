using EasyEcs;

namespace MyNamespace;

/// <summary>Marks the entities MySystem works on: replace it with the components the system needs.</summary>
public struct MySystemTarget : IComponent;

/// <summary>What MySystem does, in one sentence (one system, one responsibility; no state of its own).</summary>
public class MySystem : EcsSystem
{
    // Runs once per entity that has every component parameter and matches the filters, on each update.
    // `ref` parameters are written, `in` parameters are read; a singleton can be a parameter too.
    [ForEach, With<MySystemTarget>]
    static void Run(Entity entity)
    {
    }
}
