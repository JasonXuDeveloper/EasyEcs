using Actors;
using EasyEcs;

// What an earlier run saved: shop 1 had 2 items, bank 7 held 1,000 coins.
var storage = Storage.FromEarlierRun();

using var world = new World();
world.AddSystem<RestockSystem>();
world.AddSystem<InterestSystem>();

// Each kind: what an actor starts with. A record from storage replaces the persistent part.
var shopKind = new Prefab().SetSales(0).SetStock(5);
var bankKind = new Prefab().SetBalance(0);

var shop = Activation.Activate(world, new Shop(1), shopKind, storage);
var bank = Activation.Activate(world, new Bank(7), bankKind, storage);
var shop2 = Activation.Activate(world, new Shop(2), shopKind, storage);
Console.WriteLine($"activated: shop 1 has {shop.Stock.Items}, shop 2 (new) has {shop2.Stock.Items}, bank 7 has {bank.Balance.Coins}");

ulong lastSave = world.ChangeTick;
// Only shop 2, which is new: the records loaded with markWritten: false are not changes.
Console.WriteLine($"saved after activation: {storage.SaveChanged(world, ref lastSave)}");

for (int i = 0; i < 3; i++)
{
    bool sold = Mailman.Buy(world, new Shop(1), 1);
    Console.WriteLine($"buy 1 at shop 1: {(sold ? "sold" : "refused")}, {shop.Stock.Items} left, closed: {shop.IsClosed}");
}

Console.WriteLine($"deposit 50 at bank 7: {Mailman.Deposit(world, new Bank(7), 50)}");
Console.WriteLine($"deposit 50 at bank 8: {Mailman.Deposit(world, new Bank(8), 50)}");

world.Update<Shop>();
world.Update<Bank>();
Console.WriteLine($"after the updates: shop 1 has {shop.Stock.Items} (sold {shop.Sales.Count}), bank 7 has {bank.Balance.Coins}");

Console.WriteLine($"saved after the messages: {storage.SaveChanged(world, ref lastSave)}");

// A second world, as after a restart: the actors come back with what was saved.
using var restarted = new World();
var shopAgain = Activation.Activate(restarted, new Shop(1), shopKind, storage);
var bankAgain = Activation.Activate(restarted, new Bank(7), bankKind, storage);
Console.WriteLine($"restarted: shop 1 has {shopAgain.Stock.Items} (sold {shopAgain.Sales.Count}), bank 7 has {bankAgain.Balance.Coins}");

// CI runs the samples: fail when the restart did not bring back what was saved.
return shopAgain.Stock.Items == 3 && bankAgain.Balance.Coins == 1060 ? 0 : 1;
