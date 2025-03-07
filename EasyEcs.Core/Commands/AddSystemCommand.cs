using System;

namespace EasyEcs.Core.Commands;

public struct AddSystemCommand : ICommand
{
    public readonly Type SystemType;
    
    public AddSystemCommand(Type systemType)
    {
        SystemType = systemType;
    }
}