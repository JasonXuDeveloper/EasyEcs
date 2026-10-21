using EasyEcs;
using Godot;

namespace GodotSample;

// Components are plain data. Godot's own value types (Vector2, Color, ...) are fine as fields.
public record struct Position(Vector2 Value) : IComponent;

public record struct Velocity(Vector2 Value) : IComponent;

// The area the entities stay in, and the length of one physics tick: one value per world.
public record struct Arena(Vector2 Size) : ISingletonComponent;

public record struct Step(float Seconds) : ISingletonComponent;
