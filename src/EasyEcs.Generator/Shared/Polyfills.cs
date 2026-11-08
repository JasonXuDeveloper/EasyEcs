// Roslyn loads analyzers/generators into many hosts (VS, Rider, .NET Framework MSBuild), so this project must target
// netstandard2.0 regardless of EasyEcs.Core's target framework. netstandard2.0 lacks IsExternalInit, which the C#
// compiler needs for the `record` models used here, and System.Index, which it needs for list patterns
// (`args is [var a, var b]`: the last element is read at ^1); do not delete this file.

// ReSharper disable CheckNamespace
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System
{
    /// <summary>An index from the start or from the end of a sequence, as much of it as list patterns use.</summary>
    internal readonly struct Index
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => _value < 0 ? length + _value + 1 : _value;

        public static implicit operator Index(int value) => new(value);
    }
}
