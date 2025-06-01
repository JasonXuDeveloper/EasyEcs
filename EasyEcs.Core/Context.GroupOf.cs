using EasyEcs.Core.Components;
using EasyEcs.Core.Group;

namespace EasyEcs.Core;

public partial class Context
{
    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T> GroupOf<T>() where T : struct, IComponent
    {
        return new GroupResultEnumerator<T>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2> GroupOf<T1, T2>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3> GroupOf<T1, T2, T3>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4> GroupOf<T1, T2, T3, T4>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4, T5> GroupOf<T1, T2, T3, T4,
        T5>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4, T5>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6> GroupOf<T1, T2, T3, T4, T5, T6>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4, T5, T6>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <typeparam name="T7"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7> GroupOf<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <typeparam name="T7"></typeparam>
    /// <typeparam name="T8"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8> GroupOf<T1, T2, T3, T4, T5, T6, T7, T8>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8>(this);
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <typeparam name="T7"></typeparam>
    /// <typeparam name="T8"></typeparam>
    /// <typeparam name="T9"></typeparam>
    /// <returns></returns>
    public GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8, T9> GroupOf<T1, T2, T3, T4, T5, T6, T7, T8, T9>()
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
        where T9 : struct, IComponent
    {
        return new GroupResultEnumerator<T1, T2, T3, T4, T5, T6, T7, T8, T9>(this);
    }
}