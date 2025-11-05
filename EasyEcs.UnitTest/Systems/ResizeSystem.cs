using System;
using Cysharp.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ResizeSystem : SystemBase, IInitSystem, IExecuteSystem
{
    public UniTask OnInit(Context context)
    {
        Console.WriteLine($"ResizeSystem.OnInit called");

        // Get all entities that have ScaleComponent
        var candidates = context.GroupOf<ScaleComponent>();

        int count = 0;
        // Iterate over all entities
        foreach (var result in candidates)
        {
            count++;
            ref var scaleComponent = ref result.Component1;
            Console.WriteLine($"ResizeSystem.OnInit: Found entity, current Factor = {scaleComponent.Factor}");
            // Set the factor to 2
            scaleComponent.Factor = 2;
            Console.WriteLine($"ResizeSystem.OnInit: Set Factor to {scaleComponent.Factor}");
        }

        Console.WriteLine($"ResizeSystem.OnInit: Found {count} entities with ScaleComponent");

        return UniTask.CompletedTask;
    }

    public UniTask OnExecute(Context context)
    {
        // Should be run concurrently if possible
        Console.WriteLine($"{GetType().Name} (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");

        // Get all entities that have both ScaleComponent and SizeComponent
        var candidates = context.GroupOf<ScaleComponent, SizeComponent>();

        // Iterate over all entities
        foreach (var result in candidates)
        {
            ref var entity = ref result.Entity;
            ref var scaleComponent = ref result.Component1;
            ref var sizeComponent = ref result.Component2;

            // Get the ScaleComponent's factor
            var factor = scaleComponent.Factor;

            // Resize the size component
            sizeComponent.Width = (int)(sizeComponent.Width * factor);
            sizeComponent.Height = (int)(sizeComponent.Height * factor);

            // Remove ScaleComponent from the entity
            entity.RemoveComponent<ScaleComponent>();
        }

        return UniTask.CompletedTask;
    }
}