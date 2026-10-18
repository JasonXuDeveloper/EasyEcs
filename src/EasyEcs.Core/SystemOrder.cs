using System;

namespace EasyEcs;

/// <summary>
/// Run this system before <typeparamref name="TSystem"/> (and systems derived from it) of the same group, whatever
/// their registration order. Without an ordering, systems run in registration order; either way, of two systems whose
/// access conflicts the earlier one always finishes first, and systems with no ordering and no conflict between them
/// may run at the same time (<see cref="WorldOptions.ParallelSystems"/>). The order is resolved once, when a system is
/// added; a cycle throws there, and the analyzer reports cycles it can see (EECS019).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class UpdateBeforeAttribute<TSystem> : Attribute, ISystemOrder where TSystem : EcsSystem
{
    Type ISystemOrder.Other => typeof(TSystem);
    bool ISystemOrder.Before => true;
}

/// <summary>
/// Run this system after <typeparamref name="TSystem"/> (and systems derived from it) of the same group: see
/// <see cref="UpdateBeforeAttribute{TSystem}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class UpdateAfterAttribute<TSystem> : Attribute, ISystemOrder where TSystem : EcsSystem
{
    Type ISystemOrder.Other => typeof(TSystem);
    bool ISystemOrder.Before => false;
}

internal interface ISystemOrder
{
    Type Other { get; }
    bool Before { get; }
}
