using System;
using EasyEcs.Core.Components;

namespace EasyEcs.Core.Commands;

public struct AddComponentCommand : ICommand
{
    public readonly int Id;
    public readonly Type ComponentType;
    public readonly Action OnComponentAdded;

    public AddComponentCommand(int id, Type componentType, Action onComponentAdded)
    {
        Id = id;
        ComponentType = componentType;
        OnComponentAdded = onComponentAdded;
    }
}