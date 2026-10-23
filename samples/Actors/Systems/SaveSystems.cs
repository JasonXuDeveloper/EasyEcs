using EasyEcs;

namespace Actors;

// The save group: world.Update<Saving>() runs it, over every kind of actor. [Changed<T>] selects the rows written
// since this system last ran, so an actor is listed once per change: not again until a message or a system writes it.
// Emit<Unsaved> appends a record per row, and the records land in singleton Emitted<Unsaved> in row order when the pass
// ends: both methods append to it, the host saves and clears.

/// <summary>Lists the actors whose persistent state changed since the last save.</summary>
[EcsSystem, UpdateInGroup<Saving>]
public ref struct ListUnsavedSystem
{
    [ForEach, Changed<Stock>]
    static void ListShop(Entity shop, Emit<Unsaved> unsaved) => unsaved.Add(new Unsaved(shop.Id));

    [ForEach, Changed<Balance>]
    static void ListBank(Entity bank, Emit<Unsaved> unsaved) => unsaved.Add(new Unsaved(bank.Id));
}
