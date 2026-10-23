using EasyEcs;

namespace EasyEcs.Aot.Library;

/// <summary>A component the library's system heals, implemented by components of the using assembly.</summary>
public interface ILife : IComponent
{
    int Value { get; set; }
}

/// <summary>Heals every <typeparamref name="T"/> by one on every second update.</summary>
[UpdateInterval(2)]
public class Regen<T> : EcsSystem where T : struct, ILife
{
    [ForEach]
    static void Heal(ref T life) => life.Value++;
}
