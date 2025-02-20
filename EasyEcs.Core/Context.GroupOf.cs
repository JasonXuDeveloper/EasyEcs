using System.Collections.Generic;

namespace EasyEcs.Core;

public partial class Context
{
    /*
     * I miss C++ templates.
     */

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T)>, (Entity, T)> GroupOf<T>() where T : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T)>, (Entity, T)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T component))
                {
                    lst.Add((entity, component));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2)>, (Entity, T1, T2)> GroupOf<T1, T2>()
        where T1 : Component, new()
        where T2 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2)>, (Entity, T1, T2)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2))
                {
                    lst.Add((entity, component1, component2));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2, T3)>, (Entity, T1, T2, T3)> GroupOf<T1, T2, T3>()
        where T1 : Component, new()
        where T2 : Component, new()
        where T3 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2, T3)>, (Entity, T1, T2, T3)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2) &&
                    entity.TryGetComponent(out T3 component3))
                {
                    lst.Add((entity, component1, component2, component3));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2, T3, T4)>, (Entity, T1, T2, T3, T4)> GroupOf<T1, T2, T3, T4>()
        where T1 : Component, new()
        where T2 : Component, new()
        where T3 : Component, new()
        where T4 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2, T3, T4)>, (Entity, T1, T2, T3, T4)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2) &&
                    entity.TryGetComponent(out T3 component3) &&
                    entity.TryGetComponent(out T4 component4))
                {
                    lst.Add((entity, component1, component2, component3, component4));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2, T3, T4, T5)>, (Entity, T1, T2, T3, T4, T5)> GroupOf<T1, T2, T3, T4,
        T5>()
        where T1 : Component, new()
        where T2 : Component, new()
        where T3 : Component, new()
        where T4 : Component, new()
        where T5 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2, T3, T4, T5)>, (Entity, T1, T2, T3, T4, T5)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2) &&
                    entity.TryGetComponent(out T3 component3) &&
                    entity.TryGetComponent(out T4 component4) &&
                    entity.TryGetComponent(out T5 component5))
                {
                    lst.Add((entity, component1, component2, component3, component4, component5));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2, T3, T4, T5, T6)>,
        (Entity, T1, T2, T3, T4, T5, T6)> GroupOf<T1, T2, T3, T4, T5, T6>()
        where T1 : Component, new()
        where T2 : Component, new()
        where T3 : Component, new()
        where T4 : Component, new()
        where T5 : Component, new()
        where T6 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2, T3, T4, T5, T6)>,
            (Entity, T1, T2, T3, T4, T5, T6)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2) &&
                    entity.TryGetComponent(out T3 component3) &&
                    entity.TryGetComponent(out T4 component4) &&
                    entity.TryGetComponent(out T5 component5) &&
                    entity.TryGetComponent(out T6 component6))
                {
                    lst.Add((entity, component1, component2, component3, component4, component5, component6));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }

    /// <summary>
    /// Get all entities that have the specified components.
    /// <br/>
    /// Note: remember to dispose the returned enumerable. (i.e. using)
    /// <code>
    /// using var entities = context.GroupOf<![CDATA[<Component1, ...>]]>();
    /// </code>
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <typeparam name="T4"></typeparam>
    /// <typeparam name="T5"></typeparam>
    /// <typeparam name="T6"></typeparam>
    /// <typeparam name="T7"></typeparam>
    /// <returns></returns>
    public PooledCollection<List<(Entity, T1, T2, T3, T4, T5, T6, T7)>,
        (Entity, T1, T2, T3, T4, T5, T6, T7)> GroupOf<T1, T2, T3, T4, T5, T6, T7>()
        where T1 : Component, new()
        where T2 : Component, new()
        where T3 : Component, new()
        where T4 : Component, new()
        where T5 : Component, new()
        where T6 : Component, new()
        where T7 : Component, new()
    {
        // request a pooled enumerable
        var ret = PooledCollection<List<(Entity, T1, T2, T3, T4, T5, T6, T7)>,
            (Entity, T1, T2, T3, T4, T5, T6, T7)>.Create();
        var lst = ret.Collection;
        lst.Clear();

        // iterate over all entities and check if they have the components, then add it
        _entitiesLock.EnterReadLock();
        try
        {
            foreach (var entity in _entities)
            {
                if (entity.TryGetComponent(out T1 component1) &&
                    entity.TryGetComponent(out T2 component2) &&
                    entity.TryGetComponent(out T3 component3) &&
                    entity.TryGetComponent(out T4 component4) &&
                    entity.TryGetComponent(out T5 component5) &&
                    entity.TryGetComponent(out T6 component6) &&
                    entity.TryGetComponent(out T7 component7))
                {
                    lst.Add((entity, component1, component2, component3, component4, component5, component6,
                        component7));
                }
            }
        }
        finally
        {
            _entitiesLock.ExitReadLock();
        }

        return ret;
    }
}