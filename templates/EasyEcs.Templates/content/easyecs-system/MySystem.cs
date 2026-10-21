using EasyEcs;

namespace MyNamespace;

/// <summary>What MySystem does, in one sentence: one system, one responsibility.</summary>
public class MySystem : EcsSystem
{
    // A system keeps no state of its own: put state in components or singletons.
    //
    // A [ForEach] method runs once per entity that has all of its component parameters.
    // `ref` marks the components it writes, `in` the ones it reads; a singleton can be a
    // parameter too, and filters are attributes. Replace this sketch with your components:
    //
    // [ForEach, Without<Dead>]
    // static void Run(ref Health health, in Regeneration regeneration)
    // {
    // }
    //
    // Then add the system to the world: world.AddSystem<MySystem>();
}
