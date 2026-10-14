using EasyEcs.Core.Components;

namespace EasyEcs.UnitTest;

// Shared fixtures. Components carry a value so tests can check that data lands in the right slot.
public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Health : IComponent { public int Value; }
public struct Name : IComponent { public string Value; }   // managed reference inside a component
public struct Frozen : IComponent { }                        // tag
public struct Dead : IComponent { }                          // tag
public struct C1 : IComponent { public int V; }
public struct C2 : IComponent { public int V; }
public struct C3 : IComponent { public int V; }
public struct C4 : IComponent { public int V; }
public struct C5 : IComponent { public int V; }
public struct C6 : IComponent { public int V; }
public struct C7 : IComponent { public int V; }
public struct C8 : IComponent { public int V; }
public struct C9 : IComponent { public int V; }
public struct Clock : ISingletonComponent { public long Tick; }
public struct Config : ISingletonComponent { public int Value; }
