using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using EasyEcs.Core;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class ConcurrencyTests
{
    [Test]
    public void ConcurrentStructuralChangesAreSafe()
    {
        using var world = new World(new WorldOptions { InitialEntityCapacity = 16 });
        var ids = new ConcurrentDictionary<int, bool>();
        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 2000; i++)
            {
                var e = world.CreateEntity();
                e.Value.AddComponent<Health>();
                Assert.That(ids.TryAdd(e.Value.Id, true), Is.True, "ids are unique among live entities");
            }
        });

        Assert.That(world.EntityCount, Is.EqualTo(16000));
        Assert.That(world.Count<Health>(), Is.EqualTo(16000));
    }

    [Test]
    public void QueriesRunLockFreeWhileArchetypesAreCreated()
    {
        using var world = new World();
        world.CreateEntity().Value.AddComponent<Position>();
        bool stop = false;
        var reader = Task.Run(() =>
        {
            long n = 0;
            while (!Volatile.Read(ref stop)) n += world.Count<Position>(QueryFilter.Create().Without<Dead>());
            return n;
        });

        for (int i = 0; i < 2000; i++)
        {
            ref var e = ref world.CreateEntity().Value;
            e.AddComponent<Position>();
            if ((i & 1) != 0) e.AddComponent<Velocity>();
            if ((i & 2) != 0) e.AddComponent<Frozen>();
            if ((i & 4) != 0) e.AddComponent<Health>();
        }

        Volatile.Write(ref stop, true);
        Assert.DoesNotThrowAsync(async () => await reader);
        Assert.That(world.Count<Position>(), Is.EqualTo(2001));
    }
}
