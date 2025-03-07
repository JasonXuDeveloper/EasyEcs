using System;
using System.Linq;
using System.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Systems;
using EasyEcs.UnitTest.Components;

namespace EasyEcs.UnitTest.Systems;

public class NotUnmanagedSystem : SystemBase, IExecuteSystem
{
    public Task OnExecute(Context context)
    {
        var results = context.GroupOf<NotUnmanagedComponent>();

        Console.WriteLine($"{GetType().Name} (Priority: {Priority}, " +
                          $"Thread: {Environment.CurrentManagedThreadId}, " +
                          $"Time: {DateTime.Now:HH:mm:ss.fff})");

        foreach (var result in results)
        {
            ref var entity = ref result.Entity.Value;
            ref var component = ref result.Component;
            Console.WriteLine(
                $"Entity: {entity.Id}, Word: {component.Word}, Dictionary:" +
                $" {string.Join(", ", component.Dictionary.Select(kv => $"{kv.Key}: {kv.Value}"))}");
        }

        return Task.CompletedTask;
    }
}