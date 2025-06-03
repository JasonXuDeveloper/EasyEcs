using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public class Singleton<T> where T : struct, ISingletonComponent
{
    public bool Initialized = false;
    public T Value;
    public static Singleton<T> Instance = new();
}