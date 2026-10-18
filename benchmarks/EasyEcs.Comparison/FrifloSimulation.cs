using Friflo.Engine.ECS;
using Rng = EasyEcs.Benchmark.Scenario.Rng;

namespace EcsComparison.FrifloPort;

// Friflo.Engine.ECS: cached ArchetypeQuery objects iterated chunk by chunk (query.Chunks + Span loops, the path its
// docs recommend for performance). Dead is a tag. Queries must not change structure while they iterate, so changes go
// into the store's CommandBuffer, played back right after the system that recorded them (later systems see them, as
// in EasyEcs). Friflo ids start at 1, so the Rng gets id - 1 to roll the same numbers as the 0-based libraries.

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Health : IComponent { public int Value, Max; }
public struct AoiCell : IComponent { public int Cell; }
public struct Attack : IComponent { public int Damage, Cooldown, Interval; }
public struct Lifetime : IComponent { public int Ticks; }
public struct Burning : IComponent { public int Ticks, DamagePerTick; }
public struct Player : IComponent { public long AccountId; }
public struct Npc : IComponent { public int TemplateId; }
public struct Projectile : IComponent { public int OwnerId; }
public struct Stunned : IComponent { public int Ticks; }
public struct Dead : ITag;

public sealed class FrifloSimulation : ISimulation
{
    private readonly EntityStore _store = new();
    private readonly CommandBuffer _buffer;
    private readonly Counters _c = new();

    private readonly ArchetypeQuery<Position, Velocity> _moving;
    private readonly ArchetypeQuery<Position, AoiCell> _cells;
    private readonly ArchetypeQuery<Attack, Health> _fighters;
    private readonly ArchetypeQuery<Burning, Health> _burning;
    private readonly ArchetypeQuery<Stunned> _stunned;
    private readonly ArchetypeQuery<Health> _alive;
    private readonly ArchetypeQuery<Lifetime> _expiring;
    private readonly ArchetypeQuery<Npc> _deadNpcs;
    private readonly ArchetypeQuery<Health> _deadPlayers;
    private readonly ArchetypeQuery<Position, Velocity> _shooters;

    public FrifloSimulation(int entities)
    {
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;

        var dead = Tags.Get<Dead>();
        _moving = _store.Query<Position, Velocity>().WithoutAnyComponents(ComponentTypes.Get<Stunned>()).WithoutAnyTags(dead);
        _cells = _store.Query<Position, AoiCell>().WithoutAnyTags(dead);
        _fighters = _store.Query<Attack, Health>().WithoutAnyComponents(ComponentTypes.Get<Stunned>()).WithoutAnyTags(dead);
        _burning = _store.Query<Burning, Health>();
        _stunned = _store.Query<Stunned>();
        _alive = _store.Query<Health>().WithoutAnyTags(dead);
        _expiring = _store.Query<Lifetime>();
        _deadNpcs = _store.Query<Npc>().AllTags(dead);
        _deadPlayers = _store.Query<Health>().AllComponents(ComponentTypes.Get<Player>()).AllTags(dead);
        _shooters = _store.Query<Position, Velocity>().AllComponents(ComponentTypes.Get<Player>()).WithoutAnyTags(dead);

        _store.EnsureCapacity(entities * 2);
        for (int i = 0; i < entities; i++)
        {
            var r = Rules.Roll(i);
            if (Rules.IsPlayer(i))
                _store.CreateEntity(new Position { X = r.X, Y = r.Y }, new Velocity { X = r.VX, Y = r.VY },
                    new Health { Value = Rules.PlayerHealth, Max = Rules.PlayerHealth },
                    new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval },
                    new AoiCell { Cell = -1 }, new Player { AccountId = i });
            else
                _store.CreateEntity(new Position { X = r.X, Y = r.Y }, new Velocity { X = r.VX, Y = r.VY },
                    new Health { Value = r.Health, Max = r.Health },
                    new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval },
                    new AoiCell { Cell = -1 }, new Npc { TemplateId = i % Rules.NpcTemplates });
        }
    }

    public string Library => "Friflo";

    public void Tick()
    {
        Move();
        UpdateCells();
        Fight();
        _buffer.Playback();
        Burn();
        Recover();
        _buffer.Playback();
        CheckDeath();
        _buffer.Playback();
        Expire();
        _buffer.Playback();
        Despawn();
        Respawn();
        _buffer.Playback();
        Shoot();
        _buffer.Playback();
        _c.Tick++;
    }

    public Counters Snapshot()
    {
        _c.Entities = _store.Count;
        return _c;
    }

    public void Dispose() { }

    private void Move()
    {
        foreach (var (positions, velocities, _) in _moving.Chunks)
        {
            var ps = positions.Span;
            var vs = velocities.Span;
            for (int i = 0; i < ps.Length; i++)
            {
                ref var p = ref ps[i];
                ref var v = ref vs[i];
                p.X += v.X;
                p.Y += v.Y;
                if (p.X < 0 || p.X > Rules.MapSize) v.X = -v.X;
                if (p.Y < 0 || p.Y > Rules.MapSize) v.Y = -v.Y;
            }
        }
    }

    private void UpdateCells()
    {
        foreach (var (positions, cells, _) in _cells.Chunks)
        {
            var ps = positions.Span;
            var cs = cells.Span;
            for (int i = 0; i < ps.Length; i++)
            {
                int cell = Rules.Cell(ps[i].X, ps[i].Y);
                if (cell == cs[i].Cell) continue;
                cs[i].Cell = cell;
                _c.CellChanges++;
            }
        }
    }

    private void Fight()
    {
        foreach (var (attacks, healths, entities) in _fighters.Chunks)
        {
            var attack = attacks.Span;
            var health = healths.Span;
            var ids = entities.Ids;
            for (int i = 0; i < attack.Length; i++)
            {
                ref var a = ref attack[i];
                if (--a.Cooldown > 0) continue;
                a.Cooldown = a.Interval;
                _c.Hits++;
                health[i].Value -= a.Damage;

                int id = ids[i];
                if (Rng.Chance(_c.Tick, id - 1, Rules.BurnSalt, Rules.BurnPermille) && !entities.EntityAt(i).HasComponent<Burning>())
                    _buffer.AddComponent(id, new Burning { Ticks = Rules.BurnTicks, DamagePerTick = Rules.BurnDamage });
                else if (Rng.Chance(_c.Tick, id - 1, Rules.StunSalt, Rules.StunPermille) && !entities.EntityAt(i).HasComponent<Stunned>())
                    _buffer.AddComponent(id, new Stunned { Ticks = Rules.StunTicks });
            }
        }
    }

    private void Burn()
    {
        foreach (var (burnings, healths, entities) in _burning.Chunks)
        {
            var burning = burnings.Span;
            var health = healths.Span;
            for (int i = 0; i < burning.Length; i++)
            {
                ref var b = ref burning[i];
                health[i].Value -= b.DamagePerTick;
                if (--b.Ticks <= 0) _buffer.RemoveComponent<Burning>(entities.Ids[i]);
            }
        }
    }

    private void Recover()
    {
        foreach (var (stuns, entities) in _stunned.Chunks)
        {
            var stunned = stuns.Span;
            for (int i = 0; i < stunned.Length; i++)
                if (--stunned[i].Ticks <= 0) _buffer.RemoveComponent<Stunned>(entities.Ids[i]);
        }
    }

    private void CheckDeath()
    {
        foreach (var (healths, entities) in _alive.Chunks)
        {
            var health = healths.Span;
            for (int i = 0; i < health.Length; i++)
            {
                if (health[i].Value > 0) continue;
                _buffer.AddTag<Dead>(entities.Ids[i]);
                _c.Deaths++;
                if (entities.EntityAt(i).HasComponent<Npc>()) _c.NpcKills++;
            }
        }
    }

    private void Expire()
    {
        foreach (var (lifetimes, entities) in _expiring.Chunks)
        {
            var lifetime = lifetimes.Span;
            for (int i = 0; i < lifetime.Length; i++)
                if (--lifetime[i].Ticks <= 0) _buffer.DeleteEntity(entities.Ids[i]);
        }
    }

    private void Despawn()
    {
        foreach (var (_, entities) in _deadNpcs.Chunks)
        {
            var ids = entities.Ids;
            for (int i = 0; i < ids.Length; i++)
            {
                _buffer.DeleteEntity(ids[i]);
                int seed = Rules.RespawnSeed(ids[i] - 1);
                var r = Rules.Roll(seed);
                int npc = _buffer.CreateEntity();
                _buffer.AddComponent(npc, new Position { X = r.X, Y = r.Y });
                _buffer.AddComponent(npc, new Velocity { X = r.VX, Y = r.VY });
                _buffer.AddComponent(npc, new Health { Value = r.Health, Max = r.Health });
                _buffer.AddComponent(npc, new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval });
                _buffer.AddComponent(npc, new AoiCell { Cell = -1 });
                _buffer.AddComponent(npc, new Npc { TemplateId = seed % Rules.NpcTemplates });
            }
        }
    }

    private void Respawn()
    {
        foreach (var (healths, entities) in _deadPlayers.Chunks)
        {
            var health = healths.Span;
            for (int i = 0; i < health.Length; i++)
            {
                health[i].Value = health[i].Max;
                _buffer.RemoveTag<Dead>(entities.Ids[i]);
            }
        }
    }

    private void Shoot()
    {
        foreach (var (positions, velocities, entities) in _shooters.Chunks)
        {
            var ps = positions.Span;
            var vs = velocities.Span;
            var ids = entities.Ids;
            for (int i = 0; i < ps.Length; i++)
            {
                if (!Rng.Chance(_c.Tick, ids[i] - 1, Rules.ShootSalt, Rules.ShootPermille)) continue;
                _c.Spawns++;
                int projectile = _buffer.CreateEntity();
                _buffer.AddComponent(projectile, ps[i]);
                _buffer.AddComponent(projectile, new Velocity { X = vs[i].X * Rules.ProjectileSpeed, Y = vs[i].Y * Rules.ProjectileSpeed });
                _buffer.AddComponent(projectile, new Lifetime { Ticks = Rules.ProjectileTicks });
                _buffer.AddComponent(projectile, new Projectile { OwnerId = ids[i] - 1 });
            }
        }
    }
}
