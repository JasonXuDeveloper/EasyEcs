using System;
using System.Threading.Tasks;
using EasyEcs.Core;

namespace EasyEcs.UnitTest.Systems;

public class HelloWorldSystem : SystemBase, IExecuteSystem
{
    public override int Priority => -1;

    public Task OnExecute(Context context)
    {
        Console.WriteLine($"Hello, World! (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");
        return Task.CompletedTask;
    }
}