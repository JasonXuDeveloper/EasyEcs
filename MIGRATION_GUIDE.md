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
<LangVersion>13</LangVersion>
```

> **Note:** C# 13 is required for ref field support and other performance optimizations.

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

**Pattern 2b: Modifying Components (IMPORTANT)**

Since components are **structs**, you must modify them correctly:

✅ **Correct - Direct assignment:**
```csharp
var health = entity.AddComponent<Health>();
health.Value.HP = 100;  // Directly modify via ComponentRef
```

✅ **Correct - Using ref:**
```csharp
var health = entity.AddComponent<Health>();
ref var h = ref health.Value;  // Get ref to modify in-place
h.HP = 100;
h.MaxHP = 150;
```

❌ **WRONG - Local copy (immutable):**
```csharp
var health = entity.AddComponent<Health>();
var h = health.Value;  // Creates a COPY!
h.HP = 100;            // Modifies the COPY, not the component!
// Component unchanged - changes lost!
```

**Rule:** Always use either `compRef.Value.field = ...` or `ref compRef.Value` to modify components. Never copy the Value to a local variable for modification.

⚠️ **CRITICAL: Ref Safety Warning**

Using `ref` to hold component or entity data can be **dangerous** if arrays are resized while the ref is alive:

```csharp
// ⚠️ DANGEROUS: ref becomes invalid if arrays resize
var entity = context.CreateEntity();
var health = entity.AddComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;

// ONLY this operation invalidates refs:
context.CreateEntity();  // May resize ALL component arrays → ALL refs dangle!

h.HP = 50;  // UNSAFE! ref may point to old array or crash

// These operations are SAFE for refs:
entity.AddComponent<Armor>();        // ✅ Safe: doesn't resize arrays, only changes archetype
context.DestroyEntity(someEntity);   // ✅ Safe: doesn't resize arrays
entity.RemoveComponent<Shield>();    // ✅ Safe: doesn't resize arrays
```

**Key insight:** Component data is stored in `Context.Components[typeIndex][entityId]` arrays. Only **CreateEntity** resizes these arrays (when capacity is exceeded). AddComponent/RemoveComponent only change which archetype list an entity belongs to—they don't move or resize component data.

**When to use ref:**
- ✅ **Inside tight loops** with no structural changes:
  ```csharp
  foreach (var result in context.GroupOf<Position, Velocity>())
  {
      ref var pos = ref result.Component1.Value;
      pos.X += result.Component2.Value.X;  // Safe: no structural changes
  }
  ```

**When NOT to use ref:**
- ❌ **When creating entities** while ref is alive (may resize arrays)
- ✅ **Adding/removing components is SAFE** (doesn't resize arrays)
- ✅ **Destroying entities is SAFE** (doesn't resize arrays, but logically wrong if destroying the entity whose component you're referencing)

**Safe Alternative:**
```csharp
// ✅ SAFE: Always get fresh reference through ComponentRef
var entity = context.CreateEntity();
var health = entity.AddComponent<Health>();
health.Value.HP = 100;

// Structural changes are safe
context.CreateEntity();
entity.AddComponent<Armor>();

// Access through ComponentRef is always valid
health.Value.HP = 50;  // Safe: ComponentRef validates and accesses current data
```

**Rule of thumb:** If you're unsure, **always use `compRef.Value.field`** instead of `ref compRef.Value`. Only use `ref` for short-lived, local optimizations where you can guarantee no entity creation occurs (which could resize arrays). AddComponent/RemoveComponent are safe with refs.

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

### 2. Extended Component Types Support

- Supports up to **65,536 component types** (ushort max)
- First 256 components: inline storage (32 bytes, 2x SIMD Vector128)
- Beyond 256: automatic overflow array
- Component index changed from byte to ushort in v3.0

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

### Issue: Component Modifications Not Working

**Cause:** Copying component value to local variable (components are structs).

**Symptoms:** Changes to components don't persist, or crash when using `ref` with structural changes.

**Solution:** Use correct modification patterns:

```csharp
// ❌ Bad: Local copy
var health = entity.GetComponent<Health>();
var h = health.Value;  // COPY!
h.HP = 100;  // Lost!

// ✅ Good: Direct modification
var health = entity.GetComponent<Health>();
health.Value.HP = 100;

// ✅ Good: Using ref (only if NO structural changes)
var health = entity.GetComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;
h.MaxHP = 150;
// Don't create/destroy entities or add/remove components while holding ref!
```

See **Pattern 2b: Modifying Components** for detailed guidance.

### Issue: Crash or Corruption When Using `ref`

**Cause:** Holding `ref` to component while creating entities (which may resize arrays).

**Symptoms:** Access violations, wrong data, corrupted state after CreateEntity.

**Solution:** Don't hold `ref` across CreateEntity calls:

```csharp
// ❌ Crash: ref held during CreateEntity (array resize)
ref var health = ref entity.GetComponent<Health>().Value;
context.CreateEntity();  // May resize ALL arrays → ref invalid!
health.HP = 100;  // CRASH or wrong data!

// ✅ Safe: Use ComponentRef
var health = entity.GetComponent<Health>();
context.CreateEntity();  // Safe
health.Value.HP = 100;  // Safe: ComponentRef gets current location

// ✅ Also safe: AddComponent/RemoveComponent with ref
ref var health = ref entity.GetComponent<Health>().Value;
entity.AddComponent<Armor>();     // Safe: doesn't resize arrays
entity.RemoveComponent<Shield>(); // Safe: doesn't resize arrays
health.HP = 100;  // Safe: array wasn't resized
```

**Rule:** Only use `ref` when no entity creation will occur. AddComponent/RemoveComponent are safe with refs.

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

### 5. Modify Components In-Place

```csharp
// ❌ Bad: Creates copy, modifications lost
foreach (var result in context.GroupOf<Position, Velocity>())
{
    var pos = result.Component1.Value;  // COPY!
    pos.X += 1;  // Modifying copy, not the actual component
}

// ✅ Good: Direct modification
foreach (var result in context.GroupOf<Position, Velocity>())
{
    result.Component1.Value.X += 1;
}

// ✅ Good: Using ref for multiple modifications (SAFE: no entity creation)
foreach (var result in context.GroupOf<Position, Velocity>())
{
    ref var pos = ref result.Component1.Value;
    pos.X += result.Component2.Value.X;
    pos.Y += result.Component2.Value.Y;
}

// ⚠️ UNSAFE: ref held during CreateEntity
var health = player.GetComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;
// Spawn new enemies - may resize arrays!
for (int i = 0; i < 1000; i++)
{
    context.CreateEntity().AddComponent<Enemy>();
}
h.MaxHP = 150;  // CRASH! Arrays were resized, ref is invalid

// ✅ SAFE: Use ComponentRef across CreateEntity
var health = player.GetComponent<Health>();
health.Value.HP = 100;
// Spawn enemies - ComponentRef handles array changes
for (int i = 0; i < 1000; i++)
{
    context.CreateEntity().AddComponent<Enemy>();
}
health.Value.MaxHP = 150;  // Safe: ComponentRef gets current array location

// ✅ ALSO SAFE: ref with AddComponent/RemoveComponent (no CreateEntity)
foreach (var result in context.GroupOf<Health>())
{
    ref var health = ref result.Component1.Value;
    if (health.HP <= 0)
    {
        // AddComponent/RemoveComponent don't resize arrays - safe with ref!
        result.Entity.AddComponent<Dead>();
        health.HP = 0;  // Safe: array wasn't resized
    }
}
```

**⚠️ Ref Safety Reminder:** Only use `ref` when you're certain **no entity creation** will occur while the ref is alive (CreateEntity may resize ALL arrays). AddComponent/RemoveComponent are safe with refs. See "Pattern 2b: Modifying Components" for full details.

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
