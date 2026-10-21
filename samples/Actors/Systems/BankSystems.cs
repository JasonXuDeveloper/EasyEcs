using EasyEcs;

namespace Actors;

// The bank group: world.Update<Bank>() runs it, and its methods see only entities tagged In<Bank>.

/// <summary>Each update pays one percent of interest.</summary>
[UpdateInGroup<Bank>]
public class InterestSystem : EcsSystem
{
    // Mut<Balance>: only a balance that grew is stamped, so an account too small to earn interest is not saved again.
    [ForEach]
    static void PayInterest(Mut<Balance> balance) =>
        balance.SetIfChanged(new Balance(balance.Value.Coins + balance.Value.Coins / 100));
}
