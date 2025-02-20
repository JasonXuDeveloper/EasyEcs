namespace EasyEcs.Core;

public abstract class Component
{
    public Entity Entity => EntityRef;
    internal Entity EntityRef;
}