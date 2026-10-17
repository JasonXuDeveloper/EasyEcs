using System;
using System.Runtime.CompilerServices;

namespace EasyEcs;

// The hierarchy: ChildOf relations, children lookup, and the cascade of a parent's destruction to its children.
public sealed partial class World
{
    /// <summary>
    /// Whether <see cref="ChildOf"/> was registered in this process: until then destroying an entity has no children
    /// to look for. Set once, by <see cref="ComponentType{T}"/>.
    /// </summary>
    internal static bool HierarchyUsed;

    // The ChildOf index, once an entity is destroyed while the hierarchy is in use (or Children is called).
    private ValueIndex<ChildOf, EntityId> _children;

    // Destroyed parents whose children are still to destroy (a stack: the cascade needs no recursion). Reused.
    private EntityId[] _parents = [];
    private int _parentCount;

    /// <summary>
    /// The children of <paramref name="parent"/>: the entities whose <see cref="ChildOf"/> points at it. O(1), then O(1)
    /// per child; the first call indexes every <see cref="ChildOf"/> once. Owner thread only.
    /// </summary>
    public ValueIndex<ChildOf, EntityId>.Matches Children(EntityId parent) =>
        (_children ??= Index<ChildOf, EntityId>()).Find(parent);

    /// <summary>Have the ChildOf index ready before a destroy enters the structural gate (building it reads tables).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareCascade()
    {
        if (HierarchyUsed) PrepareChildren();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareChildren() => (_children ??= Index<ChildOf, EntityId>()).Prepare();

    /// <summary>Entity <paramref name="id"/> was destroyed: remember it if it has children. Inside the structural gate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Orphaned(EntityId id)
    {
        if (HierarchyUsed && _children != null && _children.Count(id) != 0) PushParent(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushParent(EntityId id)
    {
        if (_parentCount == _parents.Length) Array.Resize(ref _parents, Math.Max(16, _parents.Length * 2));
        _parents[_parentCount++] = id;
    }

    /// <summary>
    /// Destroy the children of the remembered parents, and theirs (a child holding cleanup components is retired, see
    /// <see cref="Retire"/>). Inside the structural gate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DestroyChildren()
    {
        while (_parentCount > 0)
        {
            var parent = _parents[--_parentCount];
            foreach (var child in _children.Find(parent)) // destroying during the walk is allowed
            {
                int id = child.Index;
                ref var meta = ref MetaRef(id);
                if (meta.Archetype.HasCleanup)
                {
                    if (!Retire(ref meta, id)) continue; // retired already: its children went then
                }
                else Free(ref meta, id);

                Orphaned(child.Id);
            }
        }
    }
}
