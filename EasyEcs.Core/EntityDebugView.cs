using System;
using System.Diagnostics;

namespace EasyEcs;

/// <summary>
/// What a debugger shows for an <see cref="Entity"/>: whether it is alive and the values of its components (disabled
/// ones marked), read when the debugger asks. Debugging only: it boxes every value.
/// </summary>
internal sealed class EntityDebugView(Entity entity)
{
    public EntityId Id => entity.Id;

    public bool IsAlive => entity.IsAlive;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public object[] Components
    {
        get
        {
            if (!entity.IsAlive) return [];
            var (table, slot) = World.Source(entity);
            var values = new System.Collections.Generic.List<object>();
            for (int c = 0; c < table.Components.Length; c++)
            {
                var info = table.Info(c);
                if (info.Owner >= 0 || info.DisabledOf >= 0) continue; // a change-tick column, a Disabled<T> tag (shown below)
                if (table.Has(c, slot)) values.Add(info.Box(table.Columns[c], slot));
                else if (table.Keeps(c, slot)) values.Add(new Disabled(info.Box(table.Columns[c], slot)));
            }

            return values.ToArray();
        }
    }

    /// <summary>A disabled component's kept value.</summary>
    [DebuggerDisplay("{Value} (disabled)")]
    private sealed record Disabled(object Value);
}
