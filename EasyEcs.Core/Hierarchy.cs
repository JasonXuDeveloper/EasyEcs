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
}
