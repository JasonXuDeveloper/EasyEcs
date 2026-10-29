using System;
using System.ComponentModel;

namespace EasyEcs;

/// <summary>
/// Records, for the generator of an assembly that references this one, which member each parameter of a public
/// component's constructor sets: metadata holds no constructor body, so without it <c>new Position(x, y)</c> in a
/// [ForEach] method of another assembly would keep the method out of its vector loop. Written by the generator into
/// the component's assembly (reference assemblies keep it, as they keep nullability), read by it in referencing ones.
/// Do not write it by hand.
/// </summary>
/// <param name="constructor">The constructor's documentation id (<c>M:Game.Position.#ctor(System.Single,System.Single)</c>).</param>
/// <param name="members">Per parameter, the field or property it sets, or an empty string for one that sets none.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true), EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ConstructorFieldsAttribute(string constructor, params string[] members) : Attribute
{
    /// <summary>The constructor's documentation id.</summary>
    public string Constructor { get; } = constructor;

    /// <summary>Per parameter, the field or property it sets ("" for none).</summary>
    public string[] Members { get; } = members;

    /// <summary>Why the members it sets are not known (its body computes them), or null.</summary>
    public string Reason { get; set; }
}

/// <summary>
/// Records, for the generator of an assembly that references this one, the body of a small public helper (a static
/// method, an extension method, a member of a struct) so that a [ForEach] method calling it inlines it into its vector
/// loop as it would from source. The body is C# with every name qualified, as static local functions: the member's
/// first (its receiver, if any, as a parameter named <c>this</c>), then the internal helpers it calls, then one
/// placeholder per field the referencing assembly cannot see, which its generated code reaches through
/// <c>[UnsafeAccessor]</c>. Do not write it by hand.
/// </summary>
/// <param name="member">The member's documentation id (<c>M:Game.Terrain.Cell(System.Single)</c>; an accessor's <c>get_X</c>).</param>
/// <param name="body">The body, or an empty string when <see cref="Reason"/> says why it was not recorded.</param>
/// <param name="accessors">
/// Per placeholder <c>__EasyEcsAccessor{i}</c> of the body, the field it stands for: <c>kind|type|name</c>, where kind is
/// <c>field</c>, <c>static field</c> or <c>static readonly field</c> and type the declaring type's metadata name.
/// </param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true), EditorBrowsable(EditorBrowsableState.Never)]
public sealed class InlineBodyAttribute(string member, string body, params string[] accessors) : Attribute
{
    /// <summary>The member's documentation id.</summary>
    public string Member { get; } = member;

    /// <summary>The body as static local functions, or an empty string.</summary>
    public string Body { get; } = body;

    /// <summary>The fields the body's placeholders stand for, in order.</summary>
    public string[] Accessors { get; } = accessors;

    /// <summary>Why the body was not recorded (a loop, a name the generated code could not reach), or null.</summary>
    public string Reason { get; set; }
}
