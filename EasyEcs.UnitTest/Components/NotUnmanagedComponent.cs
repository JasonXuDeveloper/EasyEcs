using System.Collections.Generic;
using EasyEcs.Core.Components;

namespace EasyEcs.UnitTest.Components;

public struct NotUnmanagedComponent: IComponent
{
    public string Word;
    public Dictionary<int, string> Dictionary;
}