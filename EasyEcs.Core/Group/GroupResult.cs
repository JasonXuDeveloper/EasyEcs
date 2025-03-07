namespace EasyEcs.Core.Group;

public struct GroupResult
{
    public EntityRef Entity;

    public T Component<T>() where T : struct
    {
        var entity = Entity.Value;
        var ctx = entity.Context;
        return ((T[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T))])[entity.Id];
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T>
{
    public EntityRef Entity;

    public ref T Component
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4, T5>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public ref T5 Component5
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T5[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T5))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4, T5, T6>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public ref T5 Component5
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T5[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T5))])[entity.Id];
        }
    }

    public ref T6 Component6
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T6[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T6))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4, T5, T6, T7>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public ref T5 Component5
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T5[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T5))])[entity.Id];
        }
    }

    public ref T6 Component6
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T6[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T6))])[entity.Id];
        }
    }

    public ref T7 Component7
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T7[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T7))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public ref T5 Component5
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T5[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T5))])[entity.Id];
        }
    }

    public ref T6 Component6
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T6[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T6))])[entity.Id];
        }
    }

    public ref T7 Component7
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T7[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T7))])[entity.Id];
        }
    }

    public ref T8 Component8
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T8[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T8))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}

public struct GroupResult<T1, T2, T3, T4, T5, T6, T7, T8, T9>
{
    public EntityRef Entity;

    public ref T1 Component1
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T1[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T1))])[entity.Id];
        }
    }

    public ref T2 Component2
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T2[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T2))])[entity.Id];
        }
    }

    public ref T3 Component3
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T3[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T3))])[entity.Id];
        }
    }

    public ref T4 Component4
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T4[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T4))])[entity.Id];
        }
    }

    public ref T5 Component5
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T5[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T5))])[entity.Id];
        }
    }

    public ref T6 Component6
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T6[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T6))])[entity.Id];
        }
    }

    public ref T7 Component7
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T7[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T7))])[entity.Id];
        }
    }

    public ref T8 Component8
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T8[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T8))])[entity.Id];
        }
    }

    public ref T9 Component9
    {
        get
        {
            var entity = Entity.Value;
            var ctx = entity.Context;
            return ref ((T9[])ctx.Components[ctx.TagRegistry.GetTagBitIndex(typeof(T9))])[entity.Id];
        }
    }

    public GroupResult(Entity entity)
    {
        Entity = entity;
    }
}