using System;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;
using EasyEcs.UnitTest.Systems;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SimpleTest
{
    [Test]
    public async Task TrivialTest()
    {
        var ctx = new Context();
        ctx.OnError += e => throw e;

        // Add systems to the context
        ctx.AddSystem<HelloWorldSystem>();
        ctx.AddSystem<TimeConsumingSystem>();
        ctx.AddSystem<ResizeSystem>();
        ctx.AddSystem<ModificationSystem>();

        // Add a singleton component to the context (yes, a context may have components too)
        ctx.AddSingletonComponent<FrameComponent>();

        // Create an entity
        ctx.CreateEntity(entityRef =>
        {
            ref var entity = ref entityRef.Value;
            // Add components to the entity
            entity.AddComponent<SizeComponent>();
            entity.AddComponent<ScaleComponent>(); // ResizeSystem will set factor to 2 in its OnInit
        });

        // Start the context, automatically initializing all systems,
        // then when it is out of scope, it will dispose the context
        await using (await ctx.Init())
        {
            var entity = ctx.GetEntityById(0);

            // Check if factor is 2, implying ResizeSystem has set it in its OnInit
            Assert.That(entity.Value.GetComponent<ScaleComponent>().Value.Factor, Is.EqualTo(2));

            // Set the components' values
            var sizeComponentRef = entity.Value.GetComponent<SizeComponent>();
            sizeComponentRef.Value.Width = 10;
            sizeComponentRef.Value.Height = 20;

            // Update the context
            await ctx.Update();

            // Confirm the system has removed the ScaleComponent and resized the SizeComponent
            Assert.That(sizeComponentRef.Value.Width, Is.EqualTo(20));
            Assert.That(sizeComponentRef.Value.Height, Is.EqualTo(40));

            Assert.That(entity.Value.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.Value.HasComponent<ScaleComponent>(), Is.False);

            // Update the context
            await ctx.Update();
            await ctx.Update();
            await ctx.Update();
            await ctx.Update();

            // Now the ModificationSystem should have added a ScaleComponent to the entity
            Assert.That(entity.Value.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.Value.HasComponent<ScaleComponent>(), Is.True);
            // Factor should be 0.5f
            Assert.That(entity.Value.GetComponent<ScaleComponent>().Value.Factor, Is.EqualTo(0.5f));

            // Update the context
            await ctx.Update();

            // Now the ResizeSystem should have removed the ScaleComponent and resized the SizeComponent
            Assert.That(sizeComponentRef.Value.Width, Is.EqualTo(10));
            Assert.That(sizeComponentRef.Value.Height, Is.EqualTo(20));
            Assert.That(entity.Value.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.Value.HasComponent<ScaleComponent>(), Is.False);

            // Ensure the context has only one entity
            Assert.That(ctx.EntityCount, Is.EqualTo(1));
        }

        // Ensure the context has no entities
        Assert.That(ctx.EntityCount, Is.EqualTo(0));
    }

    [Test]
    public async Task DestroyTest()
    {
        var ctx = new Context();
        ctx.OnError += e => throw e;

        // Add systems to the context
        ctx.AddSystem<ResizeSystem>();
        ctx.AddSystem<ModificationSystem>();
        ctx.AddSystem<NotUnmanagedSystem>();

        ctx.EnsureEntityCapacity(1000);

        // Create an entity
        ctx.CreateEntity(entityRef =>
        {
            ref var entity = ref entityRef.Value;
            // Add components to the entity
            entity.AddComponent<SizeComponent>();
            entity.AddComponent<ScaleComponent>();
        });

        // Start the shared context, automatically initializing all systems,
        // then when it is out of scope, it will dispose the context
        await using (await ctx.Init())
        {
            // Ensure the context has only one entity
            Assert.That(ctx.EntityCount, Is.EqualTo(1));
            var entity = ctx.GetEntityById(0);

            // Update the context
            await ctx.Update();

            // Destroy the entity
            ctx.DestroyEntity(entity);
            // Update the context
            await ctx.Update();

            // Ensure the context has no entities
            Assert.That(ctx.EntityCount, Is.EqualTo(0));

            // Update the context
            await ctx.Update();
            await ctx.Update();

            // Create 10 entities
            for (var i = 0; i < 10; i++)
            {
                int index = i;
                ctx.CreateEntity(entityRef =>
                {
                    ref var e = ref entityRef.Value;
                    e.AddComponent<SizeComponent>();
                    e.AddComponent<NotUnmanagedComponent>(compRef =>
                    {
                        ref var comp = ref compRef.Value;
                        comp.Word = $"Hello {index}";
                        comp.Dictionary = new();
                        comp.Dictionary.Add(index, Guid.NewGuid().ToString());
                    });
                });
            }

            // Ensure the context has no entities
            Assert.That(ctx.EntityCount, Is.EqualTo(0));
            // Update the context
            await ctx.Update();

            ctx.RemoveSystem<NotUnmanagedSystem>();

            // Remove 1 entity
            ctx.DestroyEntity(ctx.EntityAt(0));
            // Ensure the context has 10 entities
            Assert.That(ctx.EntityCount, Is.EqualTo(10));

            // Update the context
            await ctx.Update();

            // Ensure the context has 9 entities
            Assert.That(ctx.EntityCount, Is.EqualTo(9));

            // Update the context
            await ctx.Update();

            // Remove 1
            ctx.DestroyEntity(ctx.EntityAt(0));

            // Ensure the context has 9 entities
            Assert.That(ctx.EntityCount, Is.EqualTo(9));

            // Update the context
            await ctx.Update();

            // Ensure the context has 8 entities
            Assert.That(ctx.EntityCount, Is.EqualTo(8));
        }

        // Ensure the context has no entities
        Assert.That(ctx.EntityCount, Is.EqualTo(0));
    }
}