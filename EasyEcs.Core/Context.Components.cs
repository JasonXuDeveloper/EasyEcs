using System;
using System.Runtime.CompilerServices;
using EasyEcs.Core.Components;

namespace EasyEcs.Core;

public partial class Context
{
    /// <summary>
    /// Add a component to an entity immediately (returns ComponentRef directly).
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ComponentRef<T> AddComponent<T>(Entity entity) where T : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            // Get or register component type
            ushort componentIdx = TagRegistry.GetOrRegisterTag<T>();

            // Ensure component array exists
            EnsureComponentArray<T>(componentIdx);

            var componentArray = (T[])Components[componentIdx];

            // Update entity archetype
            ref var entityRef = ref Entities[entity.Id];

            // Remove from old archetype before modifying tag
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            // Modify tag and add to new archetype
            entityRef.Tag.SetBit(componentIdx);
            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Initialize component
            componentArray[entity.Id] = new T();

            return new ComponentRef<T>(entity.Id, EntityVersions[entity.Id], componentIdx, this);
        }
    }

    /// <summary>
    /// Remove a component from an entity immediately.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponent<T>(Entity entity) where T : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            if (!TagRegistry.TryGetTagBitIndex<T>(out var componentIdx))
                return;

            // Update entity archetype
            ref var entityRef = ref Entities[entity.Id];

            // Remove from old archetype before modifying tag
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            // Modify tag and add to new archetype
            entityRef.Tag.ClearBit(componentIdx);
            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Clear component data
            if (Components[componentIdx] is T[] componentArray)
            {
                componentArray[entity.Id] = default;
            }
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>) AddComponents<T1, T2>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            // Get or register component types
            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();

            // Ensure component arrays exist
            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);

            // Cache array references to avoid repeated lookups
            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];

            // Update entity archetype - single transition
            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Initialize components
            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>) AddComponents<T1, T2, T3>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);

            // Cache array references
            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>) AddComponents<T1, T2, T3, T4>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>) AddComponents<T1, T2, T3, T4, T5>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();
            ushort componentIdx5 = TagRegistry.GetOrRegisterTag<T5>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);
            EnsureComponentArray<T5>(componentIdx5);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];
            var componentArray5 = (T5[])Components[componentIdx5];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);
            entityRef.Tag.SetBit(componentIdx5);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();
            componentArray5[entityId] = new T5();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this),
                new ComponentRef<T5>(entityId, version, componentIdx5, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>) AddComponents<T1, T2, T3, T4, T5, T6>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();
            ushort componentIdx5 = TagRegistry.GetOrRegisterTag<T5>();
            ushort componentIdx6 = TagRegistry.GetOrRegisterTag<T6>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);
            EnsureComponentArray<T5>(componentIdx5);
            EnsureComponentArray<T6>(componentIdx6);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];
            var componentArray5 = (T5[])Components[componentIdx5];
            var componentArray6 = (T6[])Components[componentIdx6];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);
            entityRef.Tag.SetBit(componentIdx5);
            entityRef.Tag.SetBit(componentIdx6);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();
            componentArray5[entityId] = new T5();
            componentArray6[entityId] = new T6();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this),
                new ComponentRef<T5>(entityId, version, componentIdx5, this),
                new ComponentRef<T6>(entityId, version, componentIdx6, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>) AddComponents<T1, T2, T3, T4, T5, T6, T7>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();
            ushort componentIdx5 = TagRegistry.GetOrRegisterTag<T5>();
            ushort componentIdx6 = TagRegistry.GetOrRegisterTag<T6>();
            ushort componentIdx7 = TagRegistry.GetOrRegisterTag<T7>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);
            EnsureComponentArray<T5>(componentIdx5);
            EnsureComponentArray<T6>(componentIdx6);
            EnsureComponentArray<T7>(componentIdx7);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];
            var componentArray5 = (T5[])Components[componentIdx5];
            var componentArray6 = (T6[])Components[componentIdx6];
            var componentArray7 = (T7[])Components[componentIdx7];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);
            entityRef.Tag.SetBit(componentIdx5);
            entityRef.Tag.SetBit(componentIdx6);
            entityRef.Tag.SetBit(componentIdx7);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();
            componentArray5[entityId] = new T5();
            componentArray6[entityId] = new T6();
            componentArray7[entityId] = new T7();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this),
                new ComponentRef<T5>(entityId, version, componentIdx5, this),
                new ComponentRef<T6>(entityId, version, componentIdx6, this),
                new ComponentRef<T7>(entityId, version, componentIdx7, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>, ComponentRef<T8>) AddComponents<T1, T2, T3, T4, T5, T6, T7, T8>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();
            ushort componentIdx5 = TagRegistry.GetOrRegisterTag<T5>();
            ushort componentIdx6 = TagRegistry.GetOrRegisterTag<T6>();
            ushort componentIdx7 = TagRegistry.GetOrRegisterTag<T7>();
            ushort componentIdx8 = TagRegistry.GetOrRegisterTag<T8>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);
            EnsureComponentArray<T5>(componentIdx5);
            EnsureComponentArray<T6>(componentIdx6);
            EnsureComponentArray<T7>(componentIdx7);
            EnsureComponentArray<T8>(componentIdx8);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];
            var componentArray5 = (T5[])Components[componentIdx5];
            var componentArray6 = (T6[])Components[componentIdx6];
            var componentArray7 = (T7[])Components[componentIdx7];
            var componentArray8 = (T8[])Components[componentIdx8];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);
            entityRef.Tag.SetBit(componentIdx5);
            entityRef.Tag.SetBit(componentIdx6);
            entityRef.Tag.SetBit(componentIdx7);
            entityRef.Tag.SetBit(componentIdx8);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();
            componentArray5[entityId] = new T5();
            componentArray6[entityId] = new T6();
            componentArray7[entityId] = new T7();
            componentArray8[entityId] = new T8();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this),
                new ComponentRef<T5>(entityId, version, componentIdx5, this),
                new ComponentRef<T6>(entityId, version, componentIdx6, this),
                new ComponentRef<T7>(entityId, version, componentIdx7, this),
                new ComponentRef<T8>(entityId, version, componentIdx8, this)
            );
        }
    }

    /// <summary>
    /// Add multiple components to an entity immediately with a single archetype transition.
    /// Returns tuple of component refs in the same order as type parameters.
    /// Thread-safe. More efficient than calling AddComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (ComponentRef<T1>, ComponentRef<T2>, ComponentRef<T3>, ComponentRef<T4>, ComponentRef<T5>, ComponentRef<T6>, ComponentRef<T7>, ComponentRef<T8>, ComponentRef<T9>) AddComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Entity entity)
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
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            ushort componentIdx1 = TagRegistry.GetOrRegisterTag<T1>();
            ushort componentIdx2 = TagRegistry.GetOrRegisterTag<T2>();
            ushort componentIdx3 = TagRegistry.GetOrRegisterTag<T3>();
            ushort componentIdx4 = TagRegistry.GetOrRegisterTag<T4>();
            ushort componentIdx5 = TagRegistry.GetOrRegisterTag<T5>();
            ushort componentIdx6 = TagRegistry.GetOrRegisterTag<T6>();
            ushort componentIdx7 = TagRegistry.GetOrRegisterTag<T7>();
            ushort componentIdx8 = TagRegistry.GetOrRegisterTag<T8>();
            ushort componentIdx9 = TagRegistry.GetOrRegisterTag<T9>();

            EnsureComponentArray<T1>(componentIdx1);
            EnsureComponentArray<T2>(componentIdx2);
            EnsureComponentArray<T3>(componentIdx3);
            EnsureComponentArray<T4>(componentIdx4);
            EnsureComponentArray<T5>(componentIdx5);
            EnsureComponentArray<T6>(componentIdx6);
            EnsureComponentArray<T7>(componentIdx7);
            EnsureComponentArray<T8>(componentIdx8);
            EnsureComponentArray<T9>(componentIdx9);

            var componentArray1 = (T1[])Components[componentIdx1];
            var componentArray2 = (T2[])Components[componentIdx2];
            var componentArray3 = (T3[])Components[componentIdx3];
            var componentArray4 = (T4[])Components[componentIdx4];
            var componentArray5 = (T5[])Components[componentIdx5];
            var componentArray6 = (T6[])Components[componentIdx6];
            var componentArray7 = (T7[])Components[componentIdx7];
            var componentArray8 = (T8[])Components[componentIdx8];
            var componentArray9 = (T9[])Components[componentIdx9];

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            entityRef.Tag.SetBit(componentIdx1);
            entityRef.Tag.SetBit(componentIdx2);
            entityRef.Tag.SetBit(componentIdx3);
            entityRef.Tag.SetBit(componentIdx4);
            entityRef.Tag.SetBit(componentIdx5);
            entityRef.Tag.SetBit(componentIdx6);
            entityRef.Tag.SetBit(componentIdx7);
            entityRef.Tag.SetBit(componentIdx8);
            entityRef.Tag.SetBit(componentIdx9);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            componentArray1[entityId] = new T1();
            componentArray2[entityId] = new T2();
            componentArray3[entityId] = new T3();
            componentArray4[entityId] = new T4();
            componentArray5[entityId] = new T5();
            componentArray6[entityId] = new T6();
            componentArray7[entityId] = new T7();
            componentArray8[entityId] = new T8();
            componentArray9[entityId] = new T9();

            int version = EntityVersions[entityId];
            return (
                new ComponentRef<T1>(entityId, version, componentIdx1, this),
                new ComponentRef<T2>(entityId, version, componentIdx2, this),
                new ComponentRef<T3>(entityId, version, componentIdx3, this),
                new ComponentRef<T4>(entityId, version, componentIdx4, this),
                new ComponentRef<T5>(entityId, version, componentIdx5, this),
                new ComponentRef<T6>(entityId, version, componentIdx6, this),
                new ComponentRef<T7>(entityId, version, componentIdx7, this),
                new ComponentRef<T8>(entityId, version, componentIdx8, this),
                new ComponentRef<T9>(entityId, version, componentIdx9, this)
            );
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);

            if (!found1 && !found2)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Clear component data - cache arrays
            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);

            if (!found1 && !found2 && !found3)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            // Clear component data - cache arrays
            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);

            if (!found1 && !found2 && !found3 && !found4)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4, T5>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);
            bool found5 = TagRegistry.TryGetTagBitIndex<T5>(out var componentIdx5);

            if (!found1 && !found2 && !found3 && !found4 && !found5)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);
            if (found5) entityRef.Tag.ClearBit(componentIdx5);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
            if (found5 && Components[componentIdx5] is T5[] arr5)
                arr5[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);
            bool found5 = TagRegistry.TryGetTagBitIndex<T5>(out var componentIdx5);
            bool found6 = TagRegistry.TryGetTagBitIndex<T6>(out var componentIdx6);

            if (!found1 && !found2 && !found3 && !found4 && !found5 && !found6)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);
            if (found5) entityRef.Tag.ClearBit(componentIdx5);
            if (found6) entityRef.Tag.ClearBit(componentIdx6);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
            if (found5 && Components[componentIdx5] is T5[] arr5)
                arr5[entityId] = default;
            if (found6 && Components[componentIdx6] is T6[] arr6)
                arr6[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);
            bool found5 = TagRegistry.TryGetTagBitIndex<T5>(out var componentIdx5);
            bool found6 = TagRegistry.TryGetTagBitIndex<T6>(out var componentIdx6);
            bool found7 = TagRegistry.TryGetTagBitIndex<T7>(out var componentIdx7);

            if (!found1 && !found2 && !found3 && !found4 && !found5 && !found6 && !found7)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);
            if (found5) entityRef.Tag.ClearBit(componentIdx5);
            if (found6) entityRef.Tag.ClearBit(componentIdx6);
            if (found7) entityRef.Tag.ClearBit(componentIdx7);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
            if (found5 && Components[componentIdx5] is T5[] arr5)
                arr5[entityId] = default;
            if (found6 && Components[componentIdx6] is T6[] arr6)
                arr6[entityId] = default;
            if (found7 && Components[componentIdx7] is T7[] arr7)
                arr7[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8>(Entity entity)
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
        where T6 : struct, IComponent
        where T7 : struct, IComponent
        where T8 : struct, IComponent
    {
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);
            bool found5 = TagRegistry.TryGetTagBitIndex<T5>(out var componentIdx5);
            bool found6 = TagRegistry.TryGetTagBitIndex<T6>(out var componentIdx6);
            bool found7 = TagRegistry.TryGetTagBitIndex<T7>(out var componentIdx7);
            bool found8 = TagRegistry.TryGetTagBitIndex<T8>(out var componentIdx8);

            if (!found1 && !found2 && !found3 && !found4 && !found5 && !found6 && !found7 && !found8)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);
            if (found5) entityRef.Tag.ClearBit(componentIdx5);
            if (found6) entityRef.Tag.ClearBit(componentIdx6);
            if (found7) entityRef.Tag.ClearBit(componentIdx7);
            if (found8) entityRef.Tag.ClearBit(componentIdx8);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
            if (found5 && Components[componentIdx5] is T5[] arr5)
                arr5[entityId] = default;
            if (found6 && Components[componentIdx6] is T6[] arr6)
                arr6[entityId] = default;
            if (found7 && Components[componentIdx7] is T7[] arr7)
                arr7[entityId] = default;
            if (found8 && Components[componentIdx8] is T8[] arr8)
                arr8[entityId] = default;
        }
    }

    /// <summary>
    /// Remove multiple components from an entity immediately with a single archetype transition.
    /// Thread-safe. More efficient than calling RemoveComponent multiple times.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void RemoveComponents<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Entity entity)
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
        lock (_structuralLock)
        {
            if (!_activeEntityIds[entity.Id])
                throw new InvalidOperationException("Entity not found");

            bool found1 = TagRegistry.TryGetTagBitIndex<T1>(out var componentIdx1);
            bool found2 = TagRegistry.TryGetTagBitIndex<T2>(out var componentIdx2);
            bool found3 = TagRegistry.TryGetTagBitIndex<T3>(out var componentIdx3);
            bool found4 = TagRegistry.TryGetTagBitIndex<T4>(out var componentIdx4);
            bool found5 = TagRegistry.TryGetTagBitIndex<T5>(out var componentIdx5);
            bool found6 = TagRegistry.TryGetTagBitIndex<T6>(out var componentIdx6);
            bool found7 = TagRegistry.TryGetTagBitIndex<T7>(out var componentIdx7);
            bool found8 = TagRegistry.TryGetTagBitIndex<T8>(out var componentIdx8);
            bool found9 = TagRegistry.TryGetTagBitIndex<T9>(out var componentIdx9);

            if (!found1 && !found2 && !found3 && !found4 && !found5 && !found6 && !found7 && !found8 && !found9)
                return;

            ref var entityRef = ref Entities[entity.Id];
            var oldArchetype = GetOrCreateArchetype(in entityRef.Tag);
            oldArchetype.Remove(entity.Id);

            if (found1) entityRef.Tag.ClearBit(componentIdx1);
            if (found2) entityRef.Tag.ClearBit(componentIdx2);
            if (found3) entityRef.Tag.ClearBit(componentIdx3);
            if (found4) entityRef.Tag.ClearBit(componentIdx4);
            if (found5) entityRef.Tag.ClearBit(componentIdx5);
            if (found6) entityRef.Tag.ClearBit(componentIdx6);
            if (found7) entityRef.Tag.ClearBit(componentIdx7);
            if (found8) entityRef.Tag.ClearBit(componentIdx8);
            if (found9) entityRef.Tag.ClearBit(componentIdx9);

            var newArchetype = GetOrCreateArchetype(in entityRef.Tag);
            newArchetype.Add(entity.Id);

            int entityId = entity.Id;
            if (found1 && Components[componentIdx1] is T1[] arr1)
                arr1[entityId] = default;
            if (found2 && Components[componentIdx2] is T2[] arr2)
                arr2[entityId] = default;
            if (found3 && Components[componentIdx3] is T3[] arr3)
                arr3[entityId] = default;
            if (found4 && Components[componentIdx4] is T4[] arr4)
                arr4[entityId] = default;
            if (found5 && Components[componentIdx5] is T5[] arr5)
                arr5[entityId] = default;
            if (found6 && Components[componentIdx6] is T6[] arr6)
                arr6[entityId] = default;
            if (found7 && Components[componentIdx7] is T7[] arr7)
                arr7[entityId] = default;
            if (found8 && Components[componentIdx8] is T8[] arr8)
                arr8[entityId] = default;
            if (found9 && Components[componentIdx9] is T9[] arr9)
                arr9[entityId] = default;
        }
    }

    /// <summary>
    /// Ensure component array is initialized for the given component index.
    /// Must be called inside _structuralLock.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EnsureComponentArray<T>(ushort componentIdx) where T : struct, IComponent
    {
        if (Components == null || componentIdx >= Components.Length)
        {
            int newSize = Math.Max((Components?.Length ?? 0) * 2, componentIdx + 1);
            Array.Resize(ref Components, newSize);
        }

        if (Components[componentIdx] == null)
        {
            Components[componentIdx] = new T[Entities.Length];
        }
        else if (Components[componentIdx].Length < Entities.Length)
        {
            var tempArray = (T[])Components[componentIdx];
            Array.Resize(ref tempArray, Entities.Length);
            Components[componentIdx] = tempArray;
        }
    }

    /// <summary>
    /// Ensure component array is initialized for the given component index.
    /// Called lazily from ComponentRef when accessing component data.
    /// Thread-safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal T[] EnsureComponentArrayInitialized<T>(ushort componentIdx) where T : struct, IComponent
    {
        lock (_structuralLock)
        {
            // Double-check after acquiring lock
            if (Components != null &&
                componentIdx < Components.Length &&
                Components[componentIdx] != null)
            {
                return (T[])Components[componentIdx];
            }

            // Ensure Components array exists
            if (Components == null || componentIdx >= Components.Length)
            {
                int newSize = Math.Max((Components?.Length ?? 0) * 2, componentIdx + 1);
                Array.Resize(ref Components, newSize);
            }

            // Create component array if it doesn't exist
            if (Components[componentIdx] == null)
            {
                Components[componentIdx] = new T[Entities.Length];
            }

            return (T[])Components[componentIdx];
        }
    }
}
