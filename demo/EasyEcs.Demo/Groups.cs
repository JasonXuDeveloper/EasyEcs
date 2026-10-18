using EasyEcs;

namespace EasyEcs.Demo;

// The two ways the demo moves its sparks. The host updates one group or the other before the
// default group, taking turns, and times both: same work, same result, only the loop differs.

// Batch methods over spans of whole tables, vectorized with SIMD.
public struct Batched : ISystemGroup;

// One call per spark: the plain [ForEach] method.
public struct PerEntity : ISystemGroup;
