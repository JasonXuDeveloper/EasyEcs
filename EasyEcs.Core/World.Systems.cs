using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EasyEcs.Core.Systems;

namespace EasyEcs.Core;

// System scheduling. Systems run synchronously, in ascending Priority; systems sharing a priority form a bucket
// (run in parallel only with WorldOptions.Parallel). Each update calls OnUpdate() then the generated [Each] methods.
public sealed partial class World
{
    private SystemBase[] _systems = []; // sorted by priority (stable), copy-on-write
    private (int Start, int Count)[] _buckets = [];
    private ParallelOptions _parallelOptions;

    /// <summary>
    /// Raised when a system throws during <see cref="Update"/>. With no subscriber the exception propagates.
    /// </summary>
    public event Action<SystemBase, Exception> SystemError;

    /// <summary>Number of completed <see cref="Update"/> calls.</summary>
    public long UpdateCount { get; private set; }

    /// <summary>Systems in execution order.</summary>
    public IReadOnlyList<SystemBase> Systems => _systems;

    /// <summary>Create a system, attach it to this world and call its <see cref="SystemBase.OnCreate"/>.</summary>
    public T AddSystem<T>() where T : SystemBase, new()
    {
        var system = new T();
        AddSystem(system);
        return system;
    }

    /// <summary>Attach <paramref name="system"/> and call its <see cref="SystemBase.OnCreate"/>.</summary>
    public void AddSystem(SystemBase system)
    {
        if (system.World != null)
            throw new InvalidOperationException($"{system.GetType().Name} already belongs to a world");

        system.World = this;
        lock (_structuralLock)
        {
            // Stable insertion by priority: systems with equal priority keep their registration order.
            var systems = _systems;
            int index = systems.Length;
            while (index > 0 && systems[index - 1].Priority > system.Priority) index--;
            var next = new SystemBase[systems.Length + 1];
            Array.Copy(systems, 0, next, 0, index);
            next[index] = system;
            Array.Copy(systems, index, next, index + 1, systems.Length - index);
            SetSystems(next);
        }

        system.Create();
    }

    /// <summary>Remove the system of type <typeparamref name="T"/> (calls its <see cref="SystemBase.OnDestroy"/>).</summary>
    public bool RemoveSystem<T>() where T : SystemBase
    {
        SystemBase removed = null;
        lock (_structuralLock)
        {
            var systems = _systems;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] is not T) continue;
                removed = systems[i];
                var next = new SystemBase[systems.Length - 1];
                Array.Copy(systems, 0, next, 0, i);
                Array.Copy(systems, i + 1, next, i, systems.Length - i - 1);
                SetSystems(next);
                break;
            }
        }

        if (removed == null) return false;
        removed.Destroy();
        removed.World = null;
        return true;
    }

    /// <summary>Get the system of type <typeparamref name="T"/>, or null.</summary>
    public T GetSystem<T>() where T : SystemBase
    {
        var systems = _systems;
        for (int i = 0; i < systems.Length; i++)
            if (systems[i] is T match)
                return match;
        return null;
    }

    /// <summary>Run one update of every enabled system (synchronous). Allocation-free.</summary>
    public void Update()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(World));

        long update = ++UpdateCount;
        var systems = _systems;
        var buckets = _buckets;
        for (int b = 0; b < buckets.Length; b++)
        {
            var (start, count) = buckets[b];
            if (count > 1 && _options.Parallel)
            {
                RunParallel(systems, start, count, update);
            }
            else
            {
                for (int i = start; i < start + count; i++)
                    RunSystem(systems[i], update);
            }
        }
    }

    // Separate method: the lambda's closure would otherwise be allocated on every Update() call, parallel or not.
    private void RunParallel(SystemBase[] systems, int start, int count, long update)
    {
        _parallelOptions ??= new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism };
        Parallel.For(start, start + count, _parallelOptions, i => RunSystem(systems[i], update));
    }

    private void RunSystem(SystemBase system, long update)
    {
        if (!system.Enabled) return;
        int interval = system.UpdateInterval;
        if (interval > 1 && update % interval != 0) return;

        try
        {
            system.Update();
        }
        catch (Exception e) when (SystemError != null)
        {
            SystemError(system, e);
        }
    }

    private void SetSystems(SystemBase[] systems)
    {
        var buckets = new List<(int, int)>();
        for (int i = 0; i < systems.Length;)
        {
            int start = i;
            int priority = systems[i].Priority;
            while (i < systems.Length && systems[i].Priority == priority) i++;
            buckets.Add((start, i - start));
        }

        // Publish both arrays before Update can observe them (single reference swap each, readers copy locals).
        _buckets = buckets.ToArray();
        _systems = systems;
    }

    /// <summary>Destroy all systems (OnDestroy in reverse order), then release all storage.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var systems = _systems;
        for (int i = systems.Length - 1; i >= 0; i--)
        {
            try
            {
                systems[i].Destroy();
            }
            catch (Exception e) when (SystemError != null)
            {
                SystemError(systems[i], e);
            }

            systems[i].World = null;
        }

        SetSystems([]);

        Array.Clear(Meta);
        Array.Clear(Entities);
        ClearArchetypes();
        foreach (var column in Components)
            if (column != null) Array.Clear(column);
        Components = [];
        ClearSingletons();
        _emptyArchetype = null;
        _freeEntityIdCount = 0;
        _activeEntityCount = 0;
    }
}
