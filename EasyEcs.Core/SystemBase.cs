using System.Threading.Tasks;

namespace EasyEcs.Core;

/// <summary>
/// A system updates in a certain frequency and should gather some entities and apply some logic.
/// <br/>
/// Systems in a context are sorted and updated by priority.
/// </summary>
public abstract class ExecuteSystem
{
    public virtual int Priority => 0;
    public virtual int Frequency => 1;
    public abstract ValueTask Execute(Context context);

    private int _counter;

    internal ValueTask Update(Context context)
    {
        if (++_counter % Frequency == 0)
        {
            return Execute(context);
        }

        return ValueTask.CompletedTask;
    }
}