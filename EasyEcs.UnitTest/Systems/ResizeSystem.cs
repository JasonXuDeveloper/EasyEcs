using System;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ResizeSystem : SystemBase, IInitSystem, IExecuteSystem
{
    public Task OnInit(Context context)
    {
        // Get all entities that have ScaleComponent
        using var candidates = context.GroupOf<ScaleComponent>();

        // Iterate over all entities
        foreach (var (_, scaleComponent) in candidates)
        {
            // Set the factor to 2
            scaleComponent.Factor = 2;
        }

        return Task.CompletedTask;
    }

    public Task OnExecute(Context context)
    {
        // Should be run concurrently if possible
        Console.WriteLine($"{GetType()} is executing on thread {Environment.CurrentManagedThreadId}");

        // Get all entities that have both ScaleComponent and SizeComponent
        using var candidates = context.GroupOf<ScaleComponent, SizeComponent>();

        // Iterate over all entities
        foreach (var (entity, scaleComponent, sizeComponent) in candidates)
        {
            // Get the ScaleComponent's factor
            var factor = scaleComponent.Factor;

            // Resize the size component
            sizeComponent.Width = (int)(sizeComponent.Width * factor);
            sizeComponent.Height = (int)(sizeComponent.Height * factor);

            // Remove ScaleComponent from the entity
            entity.RemoveComponent<ScaleComponent>();
        }

        return Task.CompletedTask;
    }
}