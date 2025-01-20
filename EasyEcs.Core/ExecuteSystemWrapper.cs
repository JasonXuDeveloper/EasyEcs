using System.Threading.Tasks;

namespace EasyEcs.Core;

/// <summary>
/// A wrapper for an <see cref="IExecuteSystem"/> that will only execute the system at certain intervals.
/// Used by Context.
/// </summary>
internal class ExecuteSystemWrapper
{
    private readonly IExecuteSystem _system;
    private int _counter;

    internal int Priority => ((SystemBase)_system).Priority;

    public ExecuteSystemWrapper(IExecuteSystem system)
    {
        _system = system;
    }

    internal Task Update(Context context)
    {
        if (_system.ExecuteFrequency == 1 || (_counter++ > 0 && _counter % _system.ExecuteFrequency == 0))
        {
            return _system.OnExecute(context);
        }

        return Task.CompletedTask;
    }
}