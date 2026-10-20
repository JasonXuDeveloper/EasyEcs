using EasyEcs;

namespace Actors;

// The bank group: world.Update<Bank>() runs it, and its methods see only entities tagged In<Bank>.

/// <summary>Each update pays one percent of interest.</summary>
[UpdateInGroup<Bank>]
public class InterestSystem : EcsSystem
{
    [ForEach]
    static void PayInterest(ref Balance balance) => balance.Coins += balance.Coins / 100;
}
