# Migration Guide: EasyECS v2.x → v3.0

## Overview

EasyECS v3.0 is a complete architectural redesign focused on **zero-allocation immediate execution** for high-performance server applications. This guide will help you migrate from v2.x to v3.0.

## Breaking Changes Summary

### 1. No More Callbacks - Immediate Execution

**v2.x (Deferred with Callbacks):**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Position>(pos => {
        pos.Value.X = 10;
        entity.AddComponent<Velocity>(vel => {
            vel.Value.X = 5;
        });
    });
});
```

**v3.0 (Immediate with Direct Returns):**
```csharp
var entity = context.CreateEntity();
var pos = entity.AddComponent<Position>();
pos.Value.X = 10;
var vel = entity.AddComponent<Velocity>();
vel.Value.X = 5;
```

### 2. API Changes

| v2.x API | v3.0 API | Notes |
|----------|----------|-------|
| `void CreateEntity(Action<EntityRef>)` | `EntityRef CreateEntity()` | Returns entity directly |
| `void AddComponent<T>(Entity, Action<ComponentRef<T>>)` | `ComponentRef<T> AddComponent<T>(Entity)` | Returns component ref |
| `void AddComponent<T>(Action<ComponentRef<T>>)` | `ComponentRef<T> AddComponent<T>()` | On Entity struct |
| `void AddSingletonComponent<T>(Action<...>)` | `SingletonComponentRef<T> AddSingletonComponent<T>()` | Returns ref |
| `void AddSystem<T>()` | `void AddSystem<T>()` | Unchanged (immediate) |
| `void RemoveSystem<T>()` | `void RemoveSystem<T>()` | Unchanged (immediate) |

### 3. Execution Model

**v2.x:**
- Operations queued to command buffer
- Executed at end of frame
- Callbacks invoked after execution

**v3.0:**
- All operations execute immediately
- Thread-safe via .NET 8.0 Lock
- No command buffer
- No callbacks

### 4. Version Tracking

**v3.0 adds entity versioning:**
```csharp
public struct Entity
{
    public readonly int Id;
    public readonly int Version;  // NEW: Increments on destroy
    // ...
}
```

EntityRef and ComponentRef validate version on access, throwing if entity was destroyed.

## Migration Steps

### Step 1: Update Project to .NET 8.0

**Before (.csproj):**
```xml
<TargetFramework>net6.0</TargetFramework>
<LangVersion>10</LangVersion>
```

**After (.csproj):**
```xml
<TargetFramework>net8.0</TargetFramework>
<LangVersion>12</LangVersion>
```

### Step 2: Remove Callback-Based Code

**Pattern 1: Entity Creation**

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    // Use entity here
    entity.AddComponent<Position>();
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Position>();
```

**Pattern 2: Component Addition**

❌ **Old:**
```csharp
entity.AddComponent<Health>(health => {
    health.Value.HP = 100;
});
```

✅ **New:**
```csharp
var health = entity.AddComponent<Health>();
health.Value.HP = 100;
```

**Pattern 3: Nested Callbacks**

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Position>(pos => {
        pos.Value.X = 10;
        entity.AddComponent<Velocity>(vel => {
            vel.Value.X = 5;
        });
    });
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
var pos = entity.AddComponent<Position>();
pos.Value.X = 10;
var vel = entity.AddComponent<Velocity>();
vel.Value.X = 5;
```

### Step 3: Update Singleton Components

❌ **Old:**
```csharp
context.AddSingletonComponent<GameConfig>(config => {
    config.Value.MaxPlayers = 100;
});
```

✅ **New:**
```csharp
var config = context.AddSingletonComponent<GameConfig>();
config.Value.MaxPlayers = 100;
```

### Step 4: Update Entity Destruction

**No changes needed** - still immediate:
```csharp
context.DestroyEntity(entity);  // Same in both versions
```

### Step 5: Iteration (No Changes)

**GroupOf iteration is unchanged:**
```csharp
foreach (var result in context.GroupOf<Position, Velocity>())
{
    result.Component1.Value.X += result.Component2.Value.X;
}
```

### Step 6: System Implementation (Mostly Unchanged)

Systems work the same way, just use the new immediate API:

```csharp
public class MovementSystem : SystemBase, IExecuteSystem
{
    public async UniTask OnExecute(Context context)
    {
        foreach (var result in context.GroupOf<Position, Velocity>())
        {
            ref var pos = ref result.Component1.Value;
            ref var vel = ref result.Component2.Value;
            pos.X += vel.X;
        }
    }
}
```

If creating entities in systems:

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Projectile>();
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Projectile>();
```

## Performance Improvements

### Memory Allocations

**v2.x:**
- 1 closure per CreateEntity call (~24 bytes)
- 4 closures per AddComponent call (~96 bytes)
- For 10K entities with 3 components: ~2.9 MB/s allocations

**v3.0:**
- **Zero allocations** after warmup
- Pre-allocated arrays grow only once
- No closure allocations

### Execution Speed

| Operation | v2.x | v3.0 | Improvement |
|-----------|------|------|-------------|
| Tag bitwise ops | 4 scalar ops | 1 SIMD instruction | 4x faster |
| Archetype queries | O(N) scan | O(1) cached | 10-100x faster |
| Bounds checks | Normal | Unsafe (eliminated) | ~2x faster access |

### Iteration Safety

**v2.x:**
- Undefined behavior if modifying during iteration
- Could skip entities or iterate duplicates

**v3.0:**
- Tombstone pattern (-1) for safe modification
- Predictable behavior
- Fresh span retrieval handles array resize

## New Features in v3.0

### 1. Cross-Platform SIMD

Automatically uses:
- **AVX2** on x86/x64
- **AdvSimd (NEON)** on ARM
- **Scalar fallback** on other platforms

### 2. Unlimited Component Types

- First 256 components: inline (32 bytes)
- Beyond 256: automatic overflow array
- No hard limits

### 3. Archetype Query Cache

```csharp
// First call: O(N) to build cache
foreach (var result in context.GroupOf<Position, Velocity>()) { }

// Subsequent calls: O(1) cached lookup
foreach (var result in context.GroupOf<Position, Velocity>()) { }
```

### 4. Manual Defragmentation

```csharp
// Check fragmentation
var (totalSlots, alive, ratio) = context.GetFragmentationStats();
Console.WriteLine($"Fragmentation: {ratio:P}");

// Compact during loading screens
context.CompactArchetypes();
```

### 5. Pre-Allocation

```csharp
var options = new Context.Options(
    parallel: true,
    levelOfParallelism: -1,  // Use all cores
    initialEntityCapacity: 10_000  // Pre-allocate for 10K entities
);
var context = new Context(options);

// Or grow later
context.EnsureEntityCapacity(100_000);
```

## Common Migration Patterns

### Pattern 1: Unit Tests

❌ **Old:**
```csharp
[Test]
public void TestEntityCreation()
{
    bool called = false;
    context.CreateEntity(entity => {
        called = true;
        Assert.IsTrue(entity.Id >= 0);
    });
    Assert.IsTrue(called);
}
```

✅ **New:**
```csharp
[Test]
public void TestEntityCreation()
{
    var entity = context.CreateEntity();
    Assert.GreaterOrEqual(entity.Value.Id, 0);
}
```

### Pattern 2: Entity Builders

❌ **Old:**
```csharp
public void SpawnEnemy(Vector2 pos)
{
    context.CreateEntity(entity => {
        entity.AddComponent<Position>(p => p.Value = pos);
        entity.AddComponent<Enemy>(e => e.Value.HP = 100);
        entity.AddComponent<AI>();
    });
}
```

✅ **New:**
```csharp
public EntityRef SpawnEnemy(Vector2 pos)
{
    var entity = context.CreateEntity();
    entity.AddComponent<Position>().Value = pos;
    entity.AddComponent<Enemy>().Value.HP = 100;
    entity.AddComponent<AI>();
    return entity;
}
```

### Pattern 3: Component Initialization

❌ **Old:**
```csharp
entity.AddComponent<Inventory>(inv => {
    inv.Value.Capacity = 20;
    inv.Value.Items = new List<Item>();
});
```

✅ **New:**
```csharp
var inv = entity.AddComponent<Inventory>();
inv.Value.Capacity = 20;
inv.Value.Items = new List<Item>();
```

### Pattern 4: Conditional Component Addition

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Unit>();
    if (isPlayer)
    {
        entity.AddComponent<PlayerControlled>();
    }
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Unit>();
if (isPlayer)
{
    entity.AddComponent<PlayerControlled>();
}
```

## Thread Safety

### v2.x
- Command queue thread-safe
- Callbacks execute on main thread at end of frame

### v3.0
- All operations thread-safe via .NET 8.0 Lock
- Entity ID allocation lock-free (Interlocked)
- Multiple threads can create/modify entities simultaneously

```csharp
// Safe in v3.0
Parallel.For(0, 1000, i => {
    var entity = context.CreateEntity();
    entity.AddComponent<Position>();
});
```

## Troubleshooting

### Issue: "Entity has been destroyed" Exception

**Cause:** You're using a stale EntityRef/ComponentRef after entity was destroyed.

**Solution:** Entity versioning now detects this. Don't cache refs across frames.

```csharp
// ❌ Bad: Caching EntityRef
var entity = context.CreateEntity();
// ... later ...
context.DestroyEntity(entity);
var pos = entity.AddComponent<Position>();  // Throws!

// ✅ Good: Get fresh reference
if (context.TryGetEntityById(id, out var entity))
{
    entity.AddComponent<Position>();
}
```

### Issue: Tests Failing After Migration

**Cause:** Tests using callback-based assertions.

**Solution:** Update to immediate API:

```csharp
// ❌ Old
context.CreateEntity(e => Assert.NotNull(e));

// ✅ New
var entity = context.CreateEntity();
Assert.NotNull(entity);
```

### Issue: Iteration Behavior Changed

**Cause:** v3.0 uses tombstone pattern for safe iteration.

**Effect:** Entities added/removed during iteration handled safely (tombstones skipped).

**Action:** Usually no changes needed, just be aware iteration is now safe.

## Performance Tips

### 1. Pre-Allocate Entity Capacity

```csharp
context.EnsureEntityCapacity(expected_max_entities);
```

### 2. Check Fragmentation Periodically

```csharp
var (_, _, frag) = context.GetFragmentationStats();
if (frag > 0.5f)  // >50% tombstones
{
    context.CompactArchetypes();
}
```

### 3. Use Parallel Execution

```csharp
var options = new Context.Options(parallel: true);
var context = new Context(options);
```

### 4. Batch Operations

```csharp
// Efficient
for (int i = 0; i < 1000; i++)
{
    var entity = context.CreateEntity();
    entity.AddComponent<Position>();
}

// Lock acquired once per operation, not per batch
```

## Summary

**Key Takeaways:**
- ✅ Remove all callbacks
- ✅ Use direct return values
- ✅ Everything executes immediately
- ✅ Thread-safe by default
- ✅ Zero allocations after warmup
- ✅ Simpler, cleaner API

**Benefits:**
- 🚀 Massive performance improvement
- 💾 Zero memory allocations
- 🧵 Thread-safe
- 🎯 Predictable behavior
- 📝 Cleaner code

For questions or issues, please file an issue on GitHub.
