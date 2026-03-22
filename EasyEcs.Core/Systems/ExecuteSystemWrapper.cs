using System;
using Cysharp.Threading.Tasks;

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

    internal async UniTask Update(Context context, Action<Exception> onError)
    {
        int freq = System.ExecuteFrequency;
        try
        {
            if (freq == 1 || (_counter++ > 0 && _counter % freq == 0))
            {
                await System.OnExecute(context);
            }
        }
        catch (Exception e)
        {
            onError?.Invoke(e);
        }
        finally
        {
            _counter %= freq;
        }
    }
}