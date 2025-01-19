using System.Threading.Tasks;

namespace EasyEcs.Core;

/// <summary>
/// A system updates in a certain frequency and should gather some entities and apply some logic.
/// <br/>
/// Systems in a context are sorted and updated by priority.
/// </summary>
public abstract class SystemBase
{
    public virtual int Priority => 0;
    public virtual int Frequency => 1;

    private int _counter;

    internal bool ShouldUpdate()
    {
        return ++_counter % Frequency == 0;
    }
}