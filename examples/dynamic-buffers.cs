#:package EasyEcs@4.0.1
// Dynamic buffers: a list per entity, a tracked component whose writes take it by ref.
using EasyEcs;

using var world = new World();
world.AddSingleton<Drops>();
world.AddSingleton<Synced>();
world.AddSystem<PickupSystem>();
world.AddSystem<SyncBagsSystem>();
var hero = world.CreateEntity(new DynamicBuffer<Loot>(), new Collector());   // no list until the first write
var chest = world.CreateEntity(new DynamicBuffer<Loot>());
hero.Get<DynamicBuffer<Loot>>().EnsureCapacity(4);                 // reserve room: no growth later

for (int i = 0; i < 6; i++) world.Update();

foreach (var loot in hero.Read<DynamicBuffer<Loot>>())              // a read: not a change
    Console.Write($"{loot.Item} ");                                 // 3 4 5 6
Console.WriteLine($"| chest holds {chest.Read<DynamicBuffer<Loot>>().Count}, bags synced {world.Synced.Bags} times");

public record struct Loot(int Item);                                // an element: any struct
public struct Collector : IComponent;                               // a tag: picks up loot
public record struct Drops(int Next) : ISingletonComponent;
public record struct Synced(int Bags) : ISingletonComponent;

public class PickupSystem : EcsSystem
{
    [ForEach, With<Collector>]
    static void Pick(ref DynamicBuffer<Loot> bag, ref Drops drops)  // ref: a tracked write
    {
        if (bag.Count == 4) bag.RemoveAt(0);                         // a full bag drops its oldest item
        bag.Add(new Loot(++drops.Next));
    }
}

[UpdateAfter<PickupSystem>]
public class SyncBagsSystem : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Loot>>]                          // only the bags written since the last run
    static void Sync(in DynamicBuffer<Loot> bag, ref Synced synced) => synced.Bags++;
}
