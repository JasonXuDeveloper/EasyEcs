using System;

namespace JetBrains.Annotations;

/// <summary>
/// Marks an attribute whose targets are used by code the IDE cannot follow: Rider and ReSharper read it by name, from
/// any assembly, so a system ([EcsSystem], with its members) and methods marked [ForEach] or [ForEntity] (and a
/// system's hooks, OnCreate, OnUpdate and OnDestroy, members of the system) are not reported as never used (the generated code calls them through UnsafeAccessor, by
/// name). Internal: no clash with the JetBrains.Annotations package. The analyzer's suppressor does the same for
/// Roslyn's IDE0051 / IDE0052.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MeansImplicitUseAttribute : Attribute
{
    public MeansImplicitUseAttribute()
    {
    }

    public MeansImplicitUseAttribute(ImplicitUseTargetFlags targetFlags) => TargetFlags = targetFlags;

    /// <summary>What counts as used: the target itself, its members, or both.</summary>
    public ImplicitUseTargetFlags TargetFlags { get; }
}

/// <summary>The targets <see cref="MeansImplicitUseAttribute"/> marks as used (the values of the JetBrains package).</summary>
[Flags]
internal enum ImplicitUseTargetFlags
{
    Default = Itself,
    Itself = 1,
    Members = 2,
    WithMembers = Itself | Members,
}
