# MultiWorld

Scaling across cores with one world per thread: the processor count of zones, 20,000 units each, every zone updated by
its own thread with `Move`, `Damage` and `Respawn` systems. A world belongs to one thread, so nothing is locked and
nothing is shared; a server partitions its game the same way (zones, rooms, matches).

```bash
dotnet run -c Release --project samples/MultiWorld                    # 1000 updates per zone
dotnet run -c Release --project samples/MultiWorld -- --updates 200
```

Expected output (the numbers depend on the machine):

```
zone  0: 20,000 units,     ... deaths,   ... ms
...
4 worlds on 4 threads, 1000 updates each, 80,000 units: ... ms, ... M entity-updates/s
```

For measured numbers use the BenchmarkDotNet suite (`MultiWorldBenchmark` in `benchmarks/EasyEcs.Benchmark`).
