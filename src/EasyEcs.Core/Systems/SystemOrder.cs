using System;

namespace EasyEcs;

/// <summary>
/// Run this system before system <typeparamref name="TSystem"/> (an <see cref="EcsSystemAttribute">[EcsSystem]</see>, EECS077) of the same group, whatever
/// their registration order. Without an ordering, systems run in registration order; either way, of two systems whose
/// access conflicts the earlier one always finishes first, and systems with no ordering and no conflict between them
/// may run at the same time (<see cref="WorldOptions.ParallelSystems"/>). The order is resolved once, when a system is
/// added; a cycle throws there, and the analyzer reports cycles it can see (EECS019).
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class UpdateBeforeAttribute<TSystem> : Attribute, ISystemOrder where TSystem : struct, allows ref struct
{
    Type ISystemOrder.Other => typeof(TSystem);
    bool ISystemOrder.Before => true;
}

/// <summary>
/// Run this system after system <typeparamref name="TSystem"/> of the same group: see
/// <see cref="UpdateBeforeAttribute{TSystem}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class UpdateAfterAttribute<TSystem> : Attribute, ISystemOrder where TSystem : struct, allows ref struct
{
    Type ISystemOrder.Other => typeof(TSystem);
    bool ISystemOrder.Before => false;
}

internal interface ISystemOrder
{
    Type Other { get; }
    bool Before { get; }
}
