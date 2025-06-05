using EasyEcs.Core.Components;

namespace EasyEcs.UnitTest.Components;

public struct FrameComponent: ISingletonComponent
{
    public int FrameCount;
}