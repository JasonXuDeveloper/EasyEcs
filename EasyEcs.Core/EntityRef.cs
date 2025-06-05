using System;

namespace EasyEcs.Core;

public readonly struct EntityRef : IEquatable<EntityRef>
{
    private readonly int _id;
    private readonly Context _context;
    public ref Entity Value => ref _context.Entities.AsSpan()[_id];

    public EntityRef(int id, Context context)
    {
        _id = id;
        _context = context;
    }

    public static implicit operator EntityRef(Entity entity)
    {
        return new EntityRef(entity.Id, entity.Context);
    }

    public static implicit operator Entity(EntityRef entityRef)
    {
        return entityRef.Value;
    }

    public static bool operator ==(EntityRef left, EntityRef right)
    {
        return left._id == right._id && left._context == right._context;
    }

    public static bool operator !=(EntityRef left, EntityRef right)
    {
        return left._id != right._id || left._context != right._context;
    }

    public override bool Equals(object obj)
    {
        return obj is EntityRef other && this == other;
    }

    public bool Equals(EntityRef other)
    {
        return _id == other._id && Equals(_context, other._context);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_id, _context);
    }
}