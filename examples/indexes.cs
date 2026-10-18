#:package EasyEcs@4.0.1
// Value indexes: find entities by the key of an IIndexed<TKey> component; write it with Set so the index follows.
using EasyEcs;

using var world = new World();
for (int i = 0; i < 10; i++) world.CreateEntity(new Team(i % 3), new Level(i));
world.CreateEntity(new PlayerName("alice"), new Level(42));

var teams = world.TeamIndex;                                      // world.Index<Team, int>()
Console.WriteLine($"team 0: {teams.Count(0)} units");             // 4
foreach (var unit in teams.Find(1)) unit.SetTeam(2);              // moving between keys: Set
Console.WriteLine($"team 1: {teams.Count(1)}, team 2: {teams.Count(2)}");

if (world.PlayerNameIndex.TryFind("alice", out var alice))        // a unique key: one entity
    Console.WriteLine($"alice is level {alice.Level.Value}");

// Keys that compare: walk an ordered range.
Console.Write("levels 3 to 6:");
foreach (var unit in world.LevelIndex.Range(3, 6)) Console.Write($" {unit.Level.Value}");
Console.WriteLine();

public record struct Team(int Id) : IComponent, IIndexed<int> { public int Key => Id; }
public record struct PlayerName(string Value) : IComponent, IIndexed<string> { public string Key => Value; }
public record struct Level(int Value) : IComponent, IIndexed<int> { public int Key => Value; }
