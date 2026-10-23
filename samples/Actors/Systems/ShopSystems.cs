using EasyEcs;

namespace Actors;

// The shop group: world.Update<Shop>() runs it, and its methods see only entities tagged In<Shop>.

/// <summary>A closed shop gets three items and opens again.</summary>
[EcsSystem, UpdateInGroup<Shop>]
public ref struct RestockSystem
{
    [ForEach, With<Closed>]
    static void Restock(Entity shop, ref Stock stock)
    {
        stock.Items = 3;
        shop.RemoveClosed();
    }
}
