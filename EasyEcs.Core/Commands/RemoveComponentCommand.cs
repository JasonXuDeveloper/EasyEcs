using System;

namespace EasyEcs.Core.Commands;

internal struct RemoveComponentCommand : ICommand
{
    public readonly int Id;
    public readonly Type ComponentType;
    public readonly Func<byte> GetTagBitIndex;
    
    public RemoveComponentCommand(int id, Type componentType, Func<byte> getTagBitIndex)
    {
        Id = id;
        ComponentType = componentType;
        GetTagBitIndex = getTagBitIndex;
    }
}