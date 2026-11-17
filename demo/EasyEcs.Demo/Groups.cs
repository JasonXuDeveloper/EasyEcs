using EasyEcs;

namespace EasyEcs.Demo;

// The two ways the demo moves its sparks. The host updates one group or the other before the
// default group, taking turns, and times both: same methods, same result, only the loop differs.
// Only this pair is written twice, for the page to time: every other system is a plain [ForEach] method in the
// default group, and the generator makes its vector loop (a condition, a structural change under it included).

// The plain [ForEach] methods as the generator compiles them: a vector loop, several sparks per
// instruction.
public struct Vectorized : ISystemGroup;

// The same methods with SimdMode.Off: one call per spark.
public struct PerEntity : ISystemGroup;
