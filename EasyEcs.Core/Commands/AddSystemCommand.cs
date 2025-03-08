using System;

namespace EasyEcs.Core.Commands;

internal struct AddSystemCommand : ICommand
{
    public readonly Type SystemType;
    
    public AddSystemCommand(Type systemType)
    {
        SystemType = systemType;
    }
}