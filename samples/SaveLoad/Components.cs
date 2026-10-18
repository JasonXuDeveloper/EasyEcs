using EasyEcs;
using Nino.Core;

namespace SaveLoad;

// Unmanaged components (only numbers inside) are saved as their raw bytes.
public record struct Position(float X, float Y) : IComponent;

public record struct Health(int Value) : IComponent;

// A sword is an entity of its own, linked to the player who holds it with ChildOf.
public record struct Sword(int Damage) : IComponent;

// An element of a player's bag, a DynamicBuffer<Item>: a list stored on the entity.
public record struct Item(int Kind, int Count);

// Singletons are saved too.
public record struct Clock(long Tick) : ISingletonComponent;

// A managed component (it holds a string) needs a serializer. [NinoType] lets Nino
// generate one; the project references Nino.Generator for it.
[NinoType]
public partial record struct Name(string Value) : IComponent;
