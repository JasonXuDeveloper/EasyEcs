using System;
using System.Threading.Tasks;
using EasyEcs.Core;

namespace EasyEcs.UnitTest.Systems;

public class TimeConsumingSystem : SystemBase, IExecuteSystem
{
    public Task OnExecute(Context context)
    {
        Console.WriteLine($"{GetType().Name} (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");
        // presenting a time-consuming task
        // Note: since this task takes a random amount of time to complete,
        // other tasks at the same priority level should be able to run concurrently on other threads
        // if the system is set to run in parallel. Tasks with lower priority will be queued until
        // this task (or the most time-consuming task) is finished
        return Task.Delay(Random.Shared.Next(10, 100));
    }
}