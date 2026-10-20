using EasyEcs;

namespace Actors;

// A message is a job run on the one actor it is addressed to (entity.Run): one table lookup for all the components
// the job needs, the same rules as a system's pass (change ticks, refs that survive structural changes).

/// <summary>A customer buys from a shop.</summary>
public struct Buy : IForEachEntity<Stock, Sales>
{
    public int Quantity;
    public bool Sold;

    // Writes Stock (bit 0) and Sales (bit 1): both are stamped and both follow the shop if it moves table.
    public static int Writes => 0b11;

    public void Execute(Entity shop, ref Stock stock, ref Sales sales)
    {
        if (stock.Items < Quantity)
        {
            return;
        }

        stock.Items -= Quantity;
        if (stock.Items == 0)
        {
            // A structural change in the middle of the job: the shop moves table, stock and sales still point at
            // its values, and the write below reaches its new row.
            shop.SetClosed();
        }

        sales.Count += Quantity;
        Sold = true;
    }
}

/// <summary>A customer pays coins into a bank account.</summary>
public struct Deposit : IForEachEntity<Balance>, INoStructuralChanges
{
    public long Coins;

    public static int Writes => 1;

    public void Execute(Entity bank, ref Balance balance) => balance.Coins += Coins;
}

/// <summary>Delivers each message to its actor.</summary>
public static class Mailman
{
    // Built once: a filter is a value, kept in a static readonly field.
    private static readonly QueryFilter Open = QueryFilter.Create().Without<Closed>();

    /// <summary>False when the shop is not active, has closed, or has too few items.</summary>
    public static bool Buy(World world, Shop to, int quantity)
    {
        if (!world.ShopIndex.TryFind(to, out var shop))
        {
            return false;
        }

        var buy = new Buy { Quantity = quantity };
        return shop.Run<Stock, Sales, Buy>(ref buy, Open) && buy.Sold;
    }

    /// <summary>False when the bank is not active.</summary>
    public static bool Deposit(World world, Bank to, long coins)
    {
        if (!world.BankIndex.TryFind(to, out var bank))
        {
            return false;
        }

        var deposit = new Deposit { Coins = coins };
        return bank.Run<Balance, Deposit>(ref deposit);
    }
}
