using EasyEcs;

namespace Actors;

// The save group: world.Update<Saving>() runs it, over every kind of actor. [Changed<T>] selects the rows written
// since this system last ran, so an actor is listed once per change: not again until a message or a system writes it.

/// <summary>Lists the actors whose persistent state changed since the last save.</summary>
[EcsSystem, UpdateInGroup<Saving>]
public ref struct ListUnsavedSystem
{
    [ForEach, Changed<Stock>]
    static void ListShop(Entity shop, ref Unsaved unsaved) => unsaved.Actors.Add(shop.Id);

    [ForEach, Changed<Balance>]
    static void ListBank(Entity bank, ref Unsaved unsaved) => unsaved.Actors.Add(bank.Id);
}
