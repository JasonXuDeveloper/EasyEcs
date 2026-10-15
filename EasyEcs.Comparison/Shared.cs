using System;
using Rng = EasyEcs.Benchmark.Scenario.Rng;

namespace EcsComparison;

// The rules of EasyEcs.Benchmark/WorldSimulation.cs, written once so every port uses the same constants.
// The EasyEcs side runs that file unchanged; Rng is shared with it.

/// <summary>Constants and spawn rolls of the headline scenario (mirrors WorldSimulation.cs).</summary>
public static class Rules
{
    public const float MapSize = 4096;
    public const float InvCellSize = 1f / 64f;
    public const int CellsPerRow = 64;
    public const uint BurnPermille = 50, StunPermille = 20, ShootPermille = 50;
    public const int BurnSalt = 1, StunSalt = 2, ShootSalt = 3;
    public const int BurnTicks = 10, BurnDamage = 3, StunTicks = 5;
    public const int ProjectileTicks = 20, ProjectileSpeed = 4;
    public const int PlayerHealth = 500, NpcTemplates = 50;
    public const int WarmUpTicks = 200;

    /// <summary>30% players, 70% NPCs.</summary>
    public static bool IsPlayer(int index) => index % 10 < 3;

    /// <summary>Seed of the NPC that replaces a despawned one.</summary>
    public static int RespawnSeed(int deadId) => deadId * 31 + 7;

    public static int Cell(float x, float y) => (int)(y * InvCellSize) * CellsPerRow + (int)(x * InvCellSize);

    /// <summary>Initial state of an actor (the private Simulation.Roll of the scenario).</summary>
    public static Roll Roll(int seed)
    {
        uint r = Rng.Hash(seed, seed);
        int hp = 100 + (int)(r % 100);
        return new Roll
        {
            X = r % 4096, Y = (r >> 12) % 4096,
            VX = (int)(r % 7) - 3, VY = (int)((r >> 8) % 7) - 3,
            Health = hp,
            Interval = 5 + (int)(r % 10), Cooldown = (int)(r % 10), Damage = 5 + (int)(r % 5),
        };
    }
}

public struct Roll
{
    public float X, Y, VX, VY;
    public int Health, Interval, Cooldown, Damage;
}

/// <summary>World counters (the WorldStats singleton of the scenario) for libraries without singletons.</summary>
public sealed class Counters
{
    public long Tick;
    public int CellChanges, Hits, Deaths, NpcKills, Spawns;
    public int Entities;

    public override string ToString() =>
        $"tick={Tick} entities={Entities} hits={Hits} deaths={Deaths} npcKills={NpcKills} spawns={Spawns} cells={CellChanges}";
}

/// <summary>One world of one library: build, tick, read counters.</summary>
public interface ISimulation : IDisposable
{
    string Library { get; }
    void Tick();
    Counters Snapshot();
}

public static class Check
{
    /// <summary>Warm up, then require a busy world (same rule as Simulation.WarmUp).</summary>
    public static Counters WarmUp(ISimulation sim, int ticks = Rules.WarmUpTicks)
    {
        for (int i = 0; i < ticks; i++) sim.Tick();
        var c = sim.Snapshot();
        if (c.Hits == 0 || c.Deaths == 0 || c.Spawns == 0 || c.CellChanges == 0)
            throw new InvalidOperationException($"{sim.Library}: simulation is idle: {c}");
        return c;
    }

    /// <summary>
    /// Require the counters to match the EasyEcs reference within <paramref name="tolerance"/>. They cannot match
    /// exactly: entity ids feed the Rng and each library recycles ids in its own order, and libraries with deferred
    /// structural changes (command buffers) apply them at the end of a system instead of immediately.
    /// </summary>
    public static void Matches(Counters reference, Counters c, string library, double tolerance = 0.05)
    {
        Compare("hits", reference.Hits, c.Hits);
        Compare("deaths", reference.Deaths, c.Deaths);
        Compare("npcKills", reference.NpcKills, c.NpcKills);
        Compare("spawns", reference.Spawns, c.Spawns);
        Compare("cells", reference.CellChanges, c.CellChanges);
        Compare("entities", reference.Entities, c.Entities);

        void Compare(string name, int expected, int actual)
        {
            if (Math.Abs(actual - expected) > expected * tolerance)
                throw new InvalidOperationException(
                    $"{library}: {name}={actual} differs from EasyEcs {name}={expected} by more than {tolerance:P0}");
        }
    }
}
