using System.Threading.Tasks;

namespace EasyEcs.Core.Systems;

/// <summary>
/// A wrapper for an <see cref="IExecuteSystem"/> that will only execute the system at certain intervals.
/// Used by Context.
/// </summary>
internal class ExecuteSystemWrapper
{
    internal readonly IExecuteSystem System;
    private int _counter;
    
    internal ExecuteSystemWrapper(IExecuteSystem system)
    {
        System = system;
    }

    internal ValueTask Update(Context context)
    {
        if (System.ExecuteFrequency == 1 || (_counter++ > 0 && _counter % System.ExecuteFrequency == 0))
        {
            return System.OnExecute(context);
        }

        return ValueTask.CompletedTask;
    }
}