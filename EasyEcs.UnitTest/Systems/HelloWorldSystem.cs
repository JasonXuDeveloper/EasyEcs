using System;
using Cysharp.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class HelloWorldSystem : SystemBase, IExecuteSystem
{
    // Make this system to execute first
    public override int Priority => -1;

    public UniTask OnExecute(Context context)
    {
        // Get a singleton component from the context
        ref var frameComp = ref context.GetSingletonComponent<FrameComponent>().Value;

        // Print Hello, World and some info
        Console.WriteLine();
        Console.WriteLine($"Hello, World at frame {frameComp.FrameCount}! (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");
        // count frames (update singleton component data)
        frameComp.FrameCount++;
        return UniTask.CompletedTask;
    }
}