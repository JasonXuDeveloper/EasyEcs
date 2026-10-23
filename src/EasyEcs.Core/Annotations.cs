using System;

namespace JetBrains.Annotations;

/// <summary>
/// Marks an attribute whose targets are used by code the IDE cannot follow: Rider and ReSharper read it by name, from
/// any assembly, so methods marked [ForEach] or [ForEntity] are not reported as never used (the generated code calls
/// them through UnsafeAccessor, by name). Internal: no clash with the JetBrains.Annotations package. The analyzer's
/// suppressor does the same for Roslyn's IDE0051 / IDE0052.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MeansImplicitUseAttribute : Attribute;
