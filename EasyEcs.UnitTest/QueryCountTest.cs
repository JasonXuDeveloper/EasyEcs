using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class QueryCountTest
{
    [Test]
    public async Task EmptyContext_CountIsZero()
    {
        var ctx = new Context(new Context.Options(false));
        await using (await ctx.Init())
        {
            Assert.That(ctx.CountOf<SizeComponent>(), Is.EqualTo(0));
            Assert.That(ctx.AnyOf<SizeComponent>(), Is.False);

            var group = ctx.GroupOf<SizeComponent>();
            Assert.That(group.Count, Is.EqualTo(0));
            Assert.That(group.IsEmpty, Is.True);
        }
    }

    [Test]
    public async Task AfterAddingEntities_CountMatches()
    {
        var ctx = new Context(new Context.Options(false));
        await using (await ctx.Init())
        {
            var e1 = ctx.CreateEntity();
            e1.Value.AddComponent<SizeComponent>();
            e1.Value.AddComponent<ScaleComponent>();

            var e2 = ctx.CreateEntity();
            e2.Value.AddComponent<SizeComponent>();

            // SizeComponent: 2 entities
            Assert.That(ctx.CountOf<SizeComponent>(), Is.EqualTo(2));
            Assert.That(ctx.AnyOf<SizeComponent>(), Is.True);
            Assert.That(ctx.GroupOf<SizeComponent>().Count, Is.EqualTo(2));
            Assert.That(ctx.GroupOf<SizeComponent>().IsEmpty, Is.False);

            // ScaleComponent: 1 entity
            Assert.That(ctx.CountOf<ScaleComponent>(), Is.EqualTo(1));
            Assert.That(ctx.GroupOf<ScaleComponent>().Count, Is.EqualTo(1));

            // Both components: 1 entity
            Assert.That(ctx.CountOf<SizeComponent, ScaleComponent>(), Is.EqualTo(1));
            Assert.That(ctx.AnyOf<SizeComponent, ScaleComponent>(), Is.True);
            Assert.That(ctx.GroupOf<SizeComponent, ScaleComponent>().Count, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task AfterDestroyingEntities_CountDecrements()
    {
        var ctx = new Context(new Context.Options(false));
        await using (await ctx.Init())
        {
            var e1 = ctx.CreateEntity();
            e1.Value.AddComponent<SizeComponent>();

            var e2 = ctx.CreateEntity();
            e2.Value.AddComponent<SizeComponent>();

            Assert.That(ctx.CountOf<SizeComponent>(), Is.EqualTo(2));

            ctx.DestroyEntity(e1);

            Assert.That(ctx.CountOf<SizeComponent>(), Is.EqualTo(1));
            Assert.That(ctx.AnyOf<SizeComponent>(), Is.True);

            ctx.DestroyEntity(e2);

            Assert.That(ctx.CountOf<SizeComponent>(), Is.EqualTo(0));
            Assert.That(ctx.AnyOf<SizeComponent>(), Is.False);
            Assert.That(ctx.GroupOf<SizeComponent>().IsEmpty, Is.True);
        }
    }

    [Test]
    public async Task CountDoesNotAffectIteration()
    {
        var ctx = new Context(new Context.Options(false));
        await using (await ctx.Init())
        {
            var e1 = ctx.CreateEntity();
            e1.Value.AddComponent<SizeComponent>();

            var e2 = ctx.CreateEntity();
            e2.Value.AddComponent<SizeComponent>();

            var group = ctx.GroupOf<SizeComponent>();

            // Check count before iteration
            Assert.That(group.Count, Is.EqualTo(2));

            // Iterate and count manually
            int iterCount = 0;
            foreach (var _ in group)
                iterCount++;

            Assert.That(iterCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task GetFragmentationStats_NoLock()
    {
        var ctx = new Context(new Context.Options(false));
        await using (await ctx.Init())
        {
            var e1 = ctx.CreateEntity();
            e1.Value.AddComponent<SizeComponent>();

            var e2 = ctx.CreateEntity();
            e2.Value.AddComponent<SizeComponent>();

            ctx.DestroyEntity(e1);

            var (totalSlots, alive, fragRatio) = ctx.GetFragmentationStats();
            Assert.That(alive, Is.GreaterThanOrEqualTo(1));
            Assert.That(totalSlots, Is.GreaterThanOrEqualTo(alive));
            Assert.That(fragRatio, Is.GreaterThanOrEqualTo(0f));
        }
    }
}