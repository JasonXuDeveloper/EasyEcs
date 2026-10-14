using System.Threading;
using Cysharp.Threading.Tasks;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public struct QPos : IComponent
{
    public int X;
}

public struct QVel : IComponent
{
    public int X;
}

public struct QFrozen : IComponent
{
}

public struct QMarked : IComponent
{
}

/// <summary>Generated OnExecute runs both queries in declaration order.</summary>
public partial class QueryMoveSystem : SystemBase
{
    public int Visited;

    [Query, Without<QFrozen>]
    private static void Move(ref QPos p, in QVel v) => p.X += v.X;

    [Query, With<QMarked>]
    private void Count(int entityId, in QPos p, Context context)
    {
        Assert.That(context.GetEntityById(entityId).Value.HasComponent<QMarked>(), Is.True);
        Visited++;
    }
}

/// <summary>A system with its own OnExecute calls the generated Run{Query} methods.</summary>
public partial class ManualQuerySystem : SystemBase, IExecuteSystem
{
    public static int LastSum;
    private int _sum;

    public UniTask OnExecute(Context context)
    {
        _sum = 0;
        RunAdd(context);
        LastSum = _sum;
        return UniTask.CompletedTask;
    }

    [Query]
    private void Add(in QPos p) => _sum += p.X;
}

public class QueryTest
{
    [Test]
    public async Task QueryAttributeGeneratesSystem()
    {
        var ctx = new Context(new Context.Options(false));
        ctx.AddSystem<QueryMoveSystem>();

        for (int i = 0; i < 10; i++)
        {
            var e = ctx.CreateEntity();
            var (_, v) = e.Value.AddComponents<QPos, QVel>();
            v.Value.X = 1;
            if (i < 3) e.Value.AddComponent<QFrozen>();
            if (i % 2 == 0) e.Value.AddComponent<QMarked>();
        }

        await ctx.Init();
        await ctx.Update();

        int moved = 0;
        foreach (var r in ctx.GroupOf<QPos>()) moved += r.Component1.X;
        Assert.That(moved, Is.EqualTo(7), "frozen entities are skipped");
        Assert.That(ctx.CountOf<QPos>(QueryFilter.Create().Without<QFrozen>()), Is.EqualTo(7));
        Assert.That(ctx.CountOf<QPos>(QueryFilter.Create().With<QMarked>().Without<QFrozen>()), Is.EqualTo(3));
        Assert.That(ctx.AnyOf<QFrozen, QVel>(), Is.True);

        await ctx.DisposeAsync();
    }

    [Test]
    public async Task ManualOnExecuteUsesRunMethods()
    {
        var ctx = new Context(new Context.Options(false));
        ctx.AddSystem<ManualQuerySystem>();
        for (int i = 1; i <= 4; i++) ctx.CreateEntity().Value.AddComponent<QPos>().Value.X = i;
        await ctx.Init();
        await ctx.Update();
        await ctx.Update(); // the sum is reset each run, so two updates still give 1+2+3+4
        Assert.That(ManualQuerySystem.LastSum, Is.EqualTo(10));
        await ctx.DisposeAsync();
    }

    [Test]
    public void QueryCacheIsUpdatedWhenArchetypesAppear()
    {
        var ctx = new Context(new Context.Options(false));
        ctx.CreateEntity().Value.AddComponent<QPos>();
        Assert.That(ctx.CountOf<QPos>(), Is.EqualTo(1)); // caches the query

        // new archetypes created after the query was cached must be visible
        ctx.CreateEntity().Value.AddComponents<QPos, QVel>();
        ctx.CreateEntity().Value.AddComponents<QPos, QFrozen>();
        Assert.That(ctx.CountOf<QPos>(), Is.EqualTo(3));
        Assert.That(ctx.CountOf<QPos>(QueryFilter.Create().Without<QFrozen>()), Is.EqualTo(2));
    }

    [Test]
    public void ConcurrentQueriesDuringArchetypeCreation()
    {
        // Readers iterate cached query snapshots while a writer keeps creating new archetypes.
        var ctx = new Context(new Context.Options(false));
        ctx.CreateEntity().Value.AddComponent<QPos>();
        bool stop = false;
        var reader = Task.Run(() =>
        {
            long n = 0;
            while (!Volatile.Read(ref stop))
                n += ctx.CountOf<QPos>();
            return n;
        });

        for (int i = 0; i < 2000; i++)
        {
            var e = ctx.CreateEntity();
            e.Value.AddComponent<QPos>();
            if ((i & 1) == 0) e.Value.AddComponent<QVel>();
            if ((i & 2) == 0) e.Value.AddComponent<QMarked>();
            if ((i & 4) == 0) e.Value.AddComponent<QFrozen>();
        }

        Volatile.Write(ref stop, true);
        Assert.DoesNotThrowAsync(async () => await reader);
        Assert.That(ctx.CountOf<QPos>(), Is.EqualTo(2001));
    }
}
