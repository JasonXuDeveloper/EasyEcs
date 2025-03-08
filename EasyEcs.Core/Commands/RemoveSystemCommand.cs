using System;

namespace EasyEcs.Core.Commands;

internal struct RemoveSystemCommand : ICommand
{
    public readonly Type SystemType;

    public RemoveSystemCommand(Type systemType)
    {
        SystemType = systemType;
    }
}