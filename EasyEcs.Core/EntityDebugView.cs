using System.Diagnostics;

namespace EasyEcs;

/// <summary>
/// What a debugger shows for an <see cref="Entity"/>: whether it is alive and the values of its components (disabled
/// ones marked, <see cref="Entity.Describe"/>), read when the debugger asks.
/// </summary>
internal sealed class EntityDebugView(Entity entity)
{
    public EntityId Id => entity.Id;

    public bool IsAlive => entity.IsAlive;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public ComponentValue[] Components => entity.Describe();
}
