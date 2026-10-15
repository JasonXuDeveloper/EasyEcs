using Arch.Buffer;
using Arch.Core;
using Rng = EasyEcs.Benchmark.Scenario.Rng;

namespace EcsComparison.ArchPort;

// Arch: struct jobs run by World.InlineQuery / InlineEntityQuery (Arch's inlined, delegate-free query path).
// Arch does not allow structural changes while a query iterates, so they go into a CommandBuffer that is played back
// right after the system that recorded them; later systems therefore see them, as in EasyEcs. Arch's CommandBuffer
// allocates during record/playback (pooled lists and sparse sets that are cleared and regrown); that cost is Arch's own.

public struct Position { public float X, Y; }
public struct Velocity { public float X, Y; }
public struct Health { public int Value, Max; }
public struct AoiCell { public int Cell; }
public struct Attack { public int Damage, Cooldown, Interval; }
public struct Lifetime { public int Ticks; }
public struct Burning { public int Ticks, DamagePerTick; }
public struct Player { public long AccountId; }
public struct Npc { public int TemplateId; }
public struct Projectile { public int OwnerId; }
public struct Stunned { public int Ticks; }
public struct Dead;

public sealed class ArchSimulation : ISimulation
{
    private static readonly QueryDescription Moving = new QueryDescription().WithAll<Position, Velocity>().WithNone<Stunned, Dead>();
    private static readonly QueryDescription Cells = new QueryDescription().WithAll<Position, AoiCell>().WithNone<Dead>();
    private static readonly QueryDescription Fighters = new QueryDescription().WithAll<Attack, Health>().WithNone<Dead, Stunned>();
    private static readonly QueryDescription Burns = new QueryDescription().WithAll<Burning, Health>();
    private static readonly QueryDescription Stuns = new QueryDescription().WithAll<Stunned>();
    private static readonly QueryDescription Alive = new QueryDescription().WithAll<Health>().WithNone<Dead>();
    private static readonly QueryDescription Expiring = new QueryDescription().WithAll<Lifetime>();
    private static readonly QueryDescription DeadNpcs = new QueryDescription().WithAll<Dead, Npc>();
    private static readonly QueryDescription DeadPlayers = new QueryDescription().WithAll<Dead, Player, Health>();
    private static readonly QueryDescription Shooters = new QueryDescription().WithAll<Player, Position, Velocity>().WithNone<Dead>();

    private static readonly ComponentType[] NpcTypes =
    [
        Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
        Component<Attack>.ComponentType, Component<AoiCell>.ComponentType, Component<Npc>.ComponentType,
    ];

    private static readonly ComponentType[] ProjectileTypes =
    [
        Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Lifetime>.ComponentType,
        Component<Projectile>.ComponentType,
    ];

    private readonly World _world = World.Create();
    private readonly CommandBuffer _buffer = new();
    private readonly Counters _c = new();

    public ArchSimulation(int entities)
    {
        for (int i = 0; i < entities; i++)
        {
            var r = Rules.Roll(i);
            if (Rules.IsPlayer(i))
                _world.Create(new Position { X = r.X, Y = r.Y }, new Velocity { X = r.VX, Y = r.VY },
                    new Health { Value = Rules.PlayerHealth, Max = Rules.PlayerHealth },
                    new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval },
                    new AoiCell { Cell = -1 }, new Player { AccountId = i });
            else
                _world.Create(new Position { X = r.X, Y = r.Y }, new Velocity { X = r.VX, Y = r.VY },
                    new Health { Value = r.Health, Max = r.Health },
                    new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval },
                    new AoiCell { Cell = -1 }, new Npc { TemplateId = i % Rules.NpcTemplates });
        }
    }

    public string Library => "Arch";

    public void Tick()
    {
        var move = new Move();
        _world.InlineQuery<Move, Position, Velocity>(in Moving, ref move);

        var aoi = new UpdateCell { C = _c };
        _world.InlineQuery<UpdateCell, Position, AoiCell>(in Cells, ref aoi);

        var fight = new Fight { C = _c, W = _world, B = _buffer };
        _world.InlineEntityQuery<Fight, Attack, Health>(in Fighters, ref fight);
        _buffer.Playback(_world);

        var burn = new Burn { B = _buffer };
        _world.InlineEntityQuery<Burn, Burning, Health>(in Burns, ref burn);
        var recover = new Recover { B = _buffer };
        _world.InlineEntityQuery<Recover, Stunned>(in Stuns, ref recover);
        _buffer.Playback(_world);

        var death = new CheckDeath { C = _c, W = _world, B = _buffer };
        _world.InlineEntityQuery<CheckDeath, Health>(in Alive, ref death);
        _buffer.Playback(_world);

        var expire = new Expire { B = _buffer };
        _world.InlineEntityQuery<Expire, Lifetime>(in Expiring, ref expire);
        _buffer.Playback(_world);

        var despawn = new Despawn { B = _buffer };
        _world.InlineEntityQuery<Despawn, Npc>(in DeadNpcs, ref despawn);
        var respawn = new Respawn { B = _buffer };
        _world.InlineEntityQuery<Respawn, Health>(in DeadPlayers, ref respawn);
        _buffer.Playback(_world);

        var shoot = new Shoot { C = _c, B = _buffer };
        _world.InlineEntityQuery<Shoot, Position, Velocity>(in Shooters, ref shoot);
        _buffer.Playback(_world);

        _c.Tick++;
    }

    public Counters Snapshot()
    {
        _c.Entities = _world.Size;
        return _c;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        World.Destroy(_world);
    }

    private struct Move : IForEach<Position, Velocity>
    {
        public void Update(ref Position p, ref Velocity v)
        {
            p.X += v.X;
            p.Y += v.Y;
            if (p.X < 0 || p.X > Rules.MapSize) v.X = -v.X;
            if (p.Y < 0 || p.Y > Rules.MapSize) v.Y = -v.Y;
        }
    }

    private struct UpdateCell : IForEach<Position, AoiCell>
    {
        public Counters C;

        public void Update(ref Position p, ref AoiCell aoi)
        {
            int cell = Rules.Cell(p.X, p.Y);
            if (cell == aoi.Cell) return;
            aoi.Cell = cell;
            C.CellChanges++;
        }
    }

    private struct Fight : IForEachWithEntity<Attack, Health>
    {
        public Counters C;
        public World W;
        public CommandBuffer B;

        public void Update(Entity e, ref Attack attack, ref Health health)
        {
            if (--attack.Cooldown > 0) return;
            attack.Cooldown = attack.Interval;
            C.Hits++;
            health.Value -= attack.Damage;

            if (Rng.Chance(C.Tick, e.Id, Rules.BurnSalt, Rules.BurnPermille) && !W.Has<Burning>(e))
                B.Add(e, new Burning { Ticks = Rules.BurnTicks, DamagePerTick = Rules.BurnDamage });
            else if (Rng.Chance(C.Tick, e.Id, Rules.StunSalt, Rules.StunPermille) && !W.Has<Stunned>(e))
                B.Add(e, new Stunned { Ticks = Rules.StunTicks });
        }
    }

    private struct Burn : IForEachWithEntity<Burning, Health>
    {
        public CommandBuffer B;

        public void Update(Entity e, ref Burning burning, ref Health health)
        {
            health.Value -= burning.DamagePerTick;
            if (--burning.Ticks <= 0) B.Remove<Burning>(e);
        }
    }

    private struct Recover : IForEachWithEntity<Stunned>
    {
        public CommandBuffer B;

        public void Update(Entity e, ref Stunned stunned)
        {
            if (--stunned.Ticks <= 0) B.Remove<Stunned>(e);
        }
    }

    private struct CheckDeath : IForEachWithEntity<Health>
    {
        public Counters C;
        public World W;
        public CommandBuffer B;

        public void Update(Entity e, ref Health health)
        {
            if (health.Value > 0) return;
            B.Add(e, new Dead());
            C.Deaths++;
            if (W.Has<Npc>(e)) C.NpcKills++;
        }
    }

    private struct Expire : IForEachWithEntity<Lifetime>
    {
        public CommandBuffer B;

        public void Update(Entity e, ref Lifetime lifetime)
        {
            if (--lifetime.Ticks <= 0) B.Destroy(e);
        }
    }

    private struct Despawn : IForEachWithEntity<Npc>
    {
        public CommandBuffer B;

        public void Update(Entity e, ref Npc _)
        {
            B.Destroy(e);
            int seed = Rules.RespawnSeed(e.Id);
            var r = Rules.Roll(seed);
            var npc = B.Create(NpcTypes);
            B.Set(npc, new Position { X = r.X, Y = r.Y });
            B.Set(npc, new Velocity { X = r.VX, Y = r.VY });
            B.Set(npc, new Health { Value = r.Health, Max = r.Health });
            B.Set(npc, new Attack { Damage = r.Damage, Cooldown = r.Cooldown, Interval = r.Interval });
            B.Set(npc, new AoiCell { Cell = -1 });
            B.Set(npc, new Npc { TemplateId = seed % Rules.NpcTemplates });
        }
    }

    private struct Respawn : IForEachWithEntity<Health>
    {
        public CommandBuffer B;

        public void Update(Entity e, ref Health health)
        {
            health.Value = health.Max;
            B.Remove<Dead>(e);
        }
    }

    private struct Shoot : IForEachWithEntity<Position, Velocity>
    {
        public Counters C;
        public CommandBuffer B;

        public void Update(Entity e, ref Position p, ref Velocity v)
        {
            if (!Rng.Chance(C.Tick, e.Id, Rules.ShootSalt, Rules.ShootPermille)) return;
            C.Spawns++;
            var projectile = B.Create(ProjectileTypes);
            B.Set(projectile, p);
            B.Set(projectile, new Velocity { X = v.X * Rules.ProjectileSpeed, Y = v.Y * Rules.ProjectileSpeed });
            B.Set(projectile, new Lifetime { Ticks = Rules.ProjectileTicks });
            B.Set(projectile, new Projectile { OwnerId = e.Id });
        }
    }
}
