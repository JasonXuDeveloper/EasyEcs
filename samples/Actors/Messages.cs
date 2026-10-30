using EasyEcs;

namespace Actors;

// A message is a method run on the one actor it is addressed to: [EntityMethod] writes the job entity.Run runs (one
// table lookup for all the components the method takes, the same rules as a system's pass: change ticks, refs that
// survive structural changes) and an entry point, Messages.Buy(shop, quantity), that returns whether it ran.
public static class Messages
{
    /// <summary>A customer buys from a shop that has not closed.</summary>
    // Writes Stock and Sales (ref): both are stamped and both follow the shop if it moves table. It runs only for an
    // order it fills (Mailman checks the stock first): a refused order would still stamp Stock.
    [EntityMethod, Without<Closed>]
    static void Buy(Entity shop, ref Stock stock, ref Sales sales, int quantity)
    {
        stock.Items -= quantity;
        if (stock.Items == 0)
        {
            // A structural change in the middle of the method: the shop moves table, stock and sales still point at
            // its values, and the write below reaches its new row.
            shop.SetClosed();
        }

        sales.Count += quantity;
    }

    /// <summary>A customer pays coins into a bank account.</summary>
    [EntityMethod]
    static void Deposit(ref Balance balance, long coins) => balance.Coins += coins;
}

/// <summary>Delivers each message to its actor.</summary>
public static class Mailman
{
    /// <summary>False when the shop is not active, has closed, or has too few items.</summary>
    public static bool Buy(World world, Shop to, int quantity)
    {
        // Read first: a refused order writes nothing, so the next save does not store an unchanged shop.
        if (!world.ShopIndex.TryFind(to, out var shop) || shop.Stock.Items < quantity)
        {
            return false;
        }

        return Messages.Buy(shop, quantity);
    }

    /// <summary>False when the bank is not active.</summary>
    public static bool Deposit(World world, Bank to, long coins) =>
        world.BankIndex.TryFind(to, out var bank) && Messages.Deposit(bank, coins);
}
