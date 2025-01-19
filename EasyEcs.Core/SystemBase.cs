namespace EasyEcs.Core;

/// <summary>
/// A system updates in a certain frequency and should gather some entities and apply some logic.
/// <br/>
/// Systems in a context are sorted and updated by priority.
/// </summary>
public abstract class SystemBase
{
    public virtual int Priority => 0;
    public virtual int ExecuteFrequency => 1;

    private int _counter;

    internal bool ShouldExecute()
    {
        return ++_counter % ExecuteFrequency == 0;
    }
}