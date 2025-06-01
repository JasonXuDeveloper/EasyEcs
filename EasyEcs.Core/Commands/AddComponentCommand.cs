using System;

namespace EasyEcs.Core.Commands;

internal struct AddComponentCommand : ICommand
{
    public readonly int Id;
    public readonly Type ComponentType;
    public readonly Action OnComponentAdded;
    public readonly Func<bool> HasTag;
    public readonly Func<byte> GetTagBitIndex;
    public readonly Action RegisterTag;

    public AddComponentCommand(int id, Type componentType, Action onComponentAdded, Func<bool> hasTag, Func<byte> getTagBitIndex, Action registerTag)
    {
        Id = id;
        ComponentType = componentType;
        OnComponentAdded = onComponentAdded;
        HasTag = hasTag;
        GetTagBitIndex = getTagBitIndex;
        RegisterTag = registerTag;
    }
}