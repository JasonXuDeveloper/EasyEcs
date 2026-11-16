using EasyEcs;

namespace Actors;

// The save group: world.Update<Saving>() runs it, over every kind of actor. [Changed<T>] selects the rows written
// since this system last ran, so an actor is listed once per change: not again until a message or a system writes it.
// Emit<Unsaved> appends a record per row, and the records land in singleton Emitted<Unsaved> in row order when the pass
// ends. The [Drain] method then runs once on each record, in the order they were emitted, and clears them: an actor
// nobody wrote costs nothing, and none is saved twice.

/// <summary>Saves the actors whose persistent state changed since the last save.</summary>
[EcsSystem, UpdateInGroup<Saving>]
public ref struct SaveChangedSystem
{
    [ForEach, Changed<Stock>]
    static void ListShop(Entity shop, Emit<Unsaved> unsaved) => unsaved.Add(new Unsaved(shop.Id));

    [ForEach, Changed<Balance>]
    static void ListBank(Entity bank, Emit<Unsaved> unsaved) => unsaved.Add(new Unsaved(bank.Id));

    [Drain]
    static void Save(in Unsaved unsaved, World world, in Database database) =>
        database.Storage.Save(world.GetEntity(unsaved.Actor));
}
