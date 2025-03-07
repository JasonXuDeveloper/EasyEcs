using System;

namespace EasyEcs.Core.Commands;

public struct RemoveSystemCommand : ICommand
{
    public readonly Type SystemType;

    public RemoveSystemCommand(Type systemType)
    {
        SystemType = systemType;
    }
}