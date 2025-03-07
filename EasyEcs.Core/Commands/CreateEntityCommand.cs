using System;

namespace EasyEcs.Core.Commands;

internal struct CreateEntityCommand : ICommand
{
    public readonly Action<Entity> Callback;
    
    public CreateEntityCommand()
    {
        Callback =  null;
    }

    public CreateEntityCommand(Action<Entity> callback)
    {
        Callback = callback;
    }
}