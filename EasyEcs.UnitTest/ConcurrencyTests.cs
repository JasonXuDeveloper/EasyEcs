using System.Threading.Tasks;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

/// <summary>Structural operations are thread-safe (serialized by the world's lock); reads are lock-free.</summary>
public class ConcurrencyTests
{
    [Test]
    public void ConcurrentSpawnMutateAndDestroyKeepsTheWorldConsistent()
    {
        using var world = new World();
        const int threads = 8, perThread = 2_000;
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                var e = world.CreateEntity(new Health(i));
                e.Set(new Position(t, i));
                Assert.That(world.Count<Position>(), Is.GreaterThan(0)); // lock-free query cache under contention
                if (i % 2 == 0) e.Remove<Health>();
                if (i % 4 == 0) e.Destroy();
            }
        });

        Assert.That(world.EntityCount, Is.EqualTo(threads * perThread * 3 / 4));
        Assert.That(world.Count<Position>(), Is.EqualTo(world.EntityCount));
        Assert.That(world.Count<Position, Health>(), Is.EqualTo(threads * perThread / 2));
    }
}
