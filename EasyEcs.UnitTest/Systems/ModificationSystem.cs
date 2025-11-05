using System;
using Cysharp.Threading.Tasks;
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
    public UniTask OnExecute(Context context)
    {
        // Should be run concurrently if possible
        Console.WriteLine($"{GetType().Name} (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");

        var candidates = context.GroupOf<SizeComponent>();

        foreach (var result in candidates)
        {
            // Immediate execution - no callback needed!
            var comp = result.Entity.AddComponent<ScaleComponent>();
            comp.Value.Factor = 0.5f;
        }

        return UniTask.CompletedTask;
    }

    /// <summary>
    /// Make size to 0
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public UniTask OnEnd(Context context)
    {
        var candidates = context.GroupOf<SizeComponent>();

        foreach (var result in candidates)
        {
            ref var sizeComponent = ref result.Component1;
            sizeComponent.Width = 0;
            sizeComponent.Height = 0;
        }

        return UniTask.CompletedTask;
    }
}
