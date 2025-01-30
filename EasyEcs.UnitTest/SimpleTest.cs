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
        ctx.OnError += e => Assert.Fail(e.Message);
        
        // Add systems to the context
        ctx
            .AddSystem<HelloWorldSystem>()
            .AddSystem<ResizeSystem>()
            .AddSystem<ModificationSystem>();

        // Create an entity
        var entity = ctx.CreateEntity();
        // Add components to the entity
        var sizeComponent = entity.AddComponent<SizeComponent>();
        entity.AddComponent<ScaleComponent>(); // ResizeSystem will set factor to 2 in its OnInit
        
        // Start the context, automatically initializing all systems,
        // then when it is out of scope, it will dispose the context
        await using (await ctx.Init())
        {
            // Check if factor is 2, implying ResizeSystem has set it in its OnInit
            Assert.That(entity.GetComponent<ScaleComponent>().Factor, Is.EqualTo(2));
            
            // Set the components' values
            sizeComponent.Width = 10;
            sizeComponent.Height = 20;

            // Update the context
            await ctx.Update();

            // Confirm the system has removed the ScaleComponent and resized the SizeComponent
            Assert.That(sizeComponent.Width, Is.EqualTo(20));
            Assert.That(sizeComponent.Height, Is.EqualTo(40));
            Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.HasComponent<ScaleComponent>(), Is.False);

            // Update the context
            await ctx.Update();
            await ctx.Update();
            await ctx.Update();
            await ctx.Update();

            // Now the ModificationSystem should have added a ScaleComponent to the entity
            Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.HasComponent<ScaleComponent>(), Is.True);
            // Factor should be 0.5f
            Assert.That(entity.GetComponent<ScaleComponent>().Factor, Is.EqualTo(0.5f));

            // Update the context
            await ctx.Update();

            // Now the ResizeSystem should have removed the ScaleComponent and resized the SizeComponent
            Assert.That(sizeComponent.Width, Is.EqualTo(10));
            Assert.That(sizeComponent.Height, Is.EqualTo(20));
            Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
            Assert.That(entity.HasComponent<ScaleComponent>(), Is.False);

            // Ensure the context has only one entity
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(1));
        }
        
        // Ensure the context has no entities
        Assert.That(ctx.AllEntities.Count, Is.EqualTo(0));
        // Ensure size is 0, as per IEndSystem implementation in ModificationSystem
        Assert.That(sizeComponent.Width, Is.EqualTo(0));
        Assert.That(sizeComponent.Height, Is.EqualTo(0));
    }
    
    [Test]
    public async Task DestroyTest()
    {
        var ctx = new Context();
        ctx.OnError += e => Assert.Fail(e.Message);
        
        // Add systems to the context
        ctx
            .AddSystem<ResizeSystem>()
            .AddSystem<ModificationSystem>();

        // Create an entity
        var entity = ctx.CreateEntity();
        // Add components to the entity
        entity.AddComponent<SizeComponent>();
        entity.AddComponent<ScaleComponent>(); 
        
        // Start the shared context, automatically initializing all systems,
        // then when it is out of scope, it will dispose the context
        await using (await ctx.Init())
        {
            // Ensure the context has only one entity
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(1));
            
            // Update the context
            await ctx.Update();

            // Destroy the entity
            ctx.DestroyEntity(entity, true);
            
            // Ensure the context has no entities
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(0));
            
            // Update the context
            await ctx.Update();
            await ctx.Update();
            await ctx.Update();
            
            // Create 10 entities
            for (var i = 0; i < 10; i++)
            {
                var e = ctx.CreateEntity();
                e.AddComponent<SizeComponent>();
            }
            
            // Remove 1 entity
            ctx.DestroyEntity(ctx.AllEntities[0], true);
            
            // Ensure the context has 9 entities
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(9));
            
            // Update the context
            await ctx.Update();
            
            // Remove 1, not immediate
            ctx.DestroyEntity(ctx.AllEntities[0]);
            
            // Ensure the context has 9 entities
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(9));
            
            // Update the context
            await ctx.Update();
            
            // Ensure the context has 8 entities
            Assert.That(ctx.AllEntities.Count, Is.EqualTo(8));
        }
        
        // Ensure the context has no entities
        Assert.That(ctx.AllEntities.Count, Is.EqualTo(0));
    }
}