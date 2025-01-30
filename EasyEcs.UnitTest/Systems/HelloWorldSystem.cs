using System;
using System.Threading.Tasks;
using EasyEcs.Core;

namespace EasyEcs.UnitTest.Systems;

public class HelloWorldSystem : SystemBase, IExecuteSystem
{
    public Task OnExecute(Context context)
    {
        Console.WriteLine("Hello, World!");
        return Task.CompletedTask;
    }
}