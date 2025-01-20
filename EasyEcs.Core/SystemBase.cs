namespace EasyEcs.Core;

/// <summary>
/// A system updates in a certain frequency and should gather some entities and apply some logic.
/// <br/>
/// Systems in a context are sorted and invoked by priority.
/// </summary>
public abstract class SystemBase
{
    public virtual int Priority => 0;
}