# MultiWorld

Using every core with one world per thread. Read [SaveLoad](../SaveLoad) first.

What each file teaches, with its code, is on the docs site: [MultiWorld](https://ecs.xgamedev.net/en/docs/samples/multi-world)
([中文](https://ecs.xgamedev.net/zh/docs/samples/multi-world)).

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
