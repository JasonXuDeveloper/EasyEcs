namespace DocCheck;

/// <summary>
/// The docs' shared vocabulary: components every page may use without declaring them. A page that declares one of
/// these names itself replaces it (its own declaration wins). Keep it small; anything else a page declares.
/// </summary>
public static class Prelude
{
    public const string Path = "tools/DocCheck/Prelude.cs";

    public const string Source = """
        public record struct Position(float X, float Y) : IComponent;
        public record struct Velocity(float X, float Y) : IComponent;
        public record struct Health(int Value) : IComponent;
        public record struct Burning(int Ticks) : IComponent;
        public struct Dead : IComponent;
        public struct Frozen : IComponent;
        public struct Stunned : IComponent;
        """;
}
