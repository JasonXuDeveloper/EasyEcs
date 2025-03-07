using System;

namespace EasyEcs.Core.Commands;

public struct RemoveComponentCommand : ICommand
{
    public readonly int Id;
    public readonly Type ComponentType;
    
    public RemoveComponentCommand(int id, Type componentType)
    {
        Id = id;
        ComponentType = componentType;
    }
}