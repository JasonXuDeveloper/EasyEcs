using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;
using EasyEcs.UnitTest.Systems;
using NUnit.Framework;

namespace EasyEcs.UnitTest;

public class SimpleTest
{
    [Test]
    public async Task RunTest()
    {
        // Add systems to the shared context
        Context.Shared.AddSystem<ResizeSystem>();
        Context.Shared.AddSystem<ModificationSystem>();
        
        // Create an entity
        var entity = Context.Shared.CreateEntity();
        // Add components to the entity
        var sizeComponent = entity.AddComponent<SizeComponent>();
        var scaleComponent = entity.AddComponent<ScaleComponent>();

        // Set the components' values
        sizeComponent.Width = 10;
        sizeComponent.Height = 20;
        scaleComponent.Factor = 2;

        // Update the shared context
        await Context.Shared.Update();

        // Confirm the system has removed the ScaleComponent and resized the SizeComponent
        Assert.That(sizeComponent.Width, Is.EqualTo(20));
        Assert.That(sizeComponent.Height, Is.EqualTo(40));
        Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
        Assert.That(entity.HasComponent<ScaleComponent>(), Is.False);
        
        // Update the shared context
        await Context.Shared.Update();
        await Context.Shared.Update();
        await Context.Shared.Update();
        await Context.Shared.Update();
        
        // Now the ModificationSystem should have added a ScaleComponent to the entity
        Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
        Assert.That(entity.HasComponent<ScaleComponent>(), Is.True);
        // Factor should be 0.5f
        Assert.That(entity.GetComponent<ScaleComponent>().Factor, Is.EqualTo(0.5f));
        
        // Update the shared context
        await Context.Shared.Update();
        
        // Now the ResizeSystem should have removed the ScaleComponent and resized the SizeComponent
        Assert.That(sizeComponent.Width, Is.EqualTo(10));
        Assert.That(sizeComponent.Height, Is.EqualTo(20));
        Assert.That(entity.HasComponent<SizeComponent>(), Is.True);
        Assert.That(entity.HasComponent<ScaleComponent>(), Is.False);
        
        // Ensure the context has only one entity
        Assert.That(Context.Shared.AllEntities.Count, Is.EqualTo(1));
        
        // Remove the entity
        Context.Shared.DestroyEntity(entity);
        // Update the shared context
        await Context.Shared.Update();
        
        // Ensure the context has no entities
        Assert.That(Context.Shared.AllEntities.Count, Is.EqualTo(0));
    }
}