using System;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class ModificationSystem : SystemBase, IExecuteSystem, IEndSystem
{
    public int ExecuteFrequency => 5;
    public override int Priority => 1;

    /// <summary>
    /// Halve the size of all entities that have a SizeComponent
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public Task OnExecute(Context context)
    {
        // Should be run concurrently if possible
        Console.WriteLine($"{GetType().Name} (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");

        var candidates = context.GroupOf(typeof(SizeComponent));

        foreach (var result in candidates)
        {
            result.Entity.Value.AddComponent<ScaleComponent>(
                comp =>
                    comp.Value.Factor = 0.5f);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Make size to 0
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public Task OnEnd(Context context)
    {
        var candidates = context.GroupOf<SizeComponent>();

        foreach (var result in candidates)
        {
            ref var sizeComponent = ref result.Component;
            sizeComponent.Width = 0;
            sizeComponent.Height = 0;
        }

        return Task.CompletedTask;
    }
}