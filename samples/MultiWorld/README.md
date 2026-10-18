# MultiWorld

Using every core with one world per thread. Read [SaveLoad](../SaveLoad) first.

## What you learn

- **A world belongs to one thread.** A server splits its game into zones (maps, rooms, matches) that share
  nothing, and gives each zone its own world on its own thread. Nothing is locked and nothing is shared.
- Each zone here is the same small game: 20,000 units, a `MoveSystem`, a `DamageSystem` (units far from the
  centre lose health) and a `RespawnSystem` (the dead come back at the centre). The `Tally` singleton counts the
  deaths of its own world.
- One zone per processor, all updated at once.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | `Position`, `Velocity`, `Health` and the `Tally` singleton |
| [Systems.cs](Systems.cs) | `MoveSystem`, `DamageSystem`, `RespawnSystem` |
| [Zone.cs](Zone.cs) | `Zone.Run`: builds one zone's world, updates it, reports a `ZoneResult` |
| [Program.cs](Program.cs) | starts one thread per zone, waits for them, prints the results |

## Run it

```bash
dotnet run -c Release --project samples/MultiWorld                    # 1000 updates per zone
dotnet run -c Release --project samples/MultiWorld -- --updates 200
```

## Expected output

On a 4-core machine. The deaths are the same on every run; the times and the throughput depend on the machine.

```text
zone  0: 20,000 units,  51,430 deaths,  169.3 ms
zone  1: 20,000 units,  51,428 deaths,  188.4 ms
zone  2: 20,000 units,  51,429 deaths,  165.6 ms
zone  3: 20,000 units,  51,426 deaths,  150.6 ms
4 worlds on 4 threads, 80,000 units, 1000 updates each
253 ms, 316.2 M entity-updates/s
```

This is a demonstration, not a measurement: for numbers use the BenchmarkDotNet suite (`MultiWorldBenchmark` in
`benchmarks/EasyEcs.Benchmark`).

Next: [HotReload](../HotReload), replacing game logic while the world runs.
