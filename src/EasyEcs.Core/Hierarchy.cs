using System.Runtime.CompilerServices;

namespace EasyEcs;

/// <summary>
/// The hierarchy relation: the entity is a child of <see cref="Parent"/>. Destroying a parent destroys its children,
/// and theirs (<see cref="World.Children"/> lists them). An indexed relation like any other: write it with <c>Set</c>,
/// re-parent by setting a new value, detach by removing it.
/// </summary>
public record struct ChildOf(EntityId Parent) : IComponent, IIndexed<EntityId>
{
    /// <inheritdoc/>
    public EntityId Key => Parent;

    // What the generator registers for user components (SavedTypes): loadable by its type id, its parent rewritten by migrations.
#pragma warning disable CA2255 // a library initializer on purpose: saves must find ChildOf before any world uses it
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        SavedTypes.Component<ChildOf>();
        SavedTypes.EntityIds<ChildOf>(static (ref value) => ref ParentField(ref value));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Parent>k__BackingField")]
    private static extern ref EntityId ParentField(ref ChildOf value);
}
