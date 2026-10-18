# HelloWorld

The smallest complete EasyEcs program: two components (`Position`, `Velocity`), a tag (`Player`), a singleton
(`Clock`) and three systems, each with one job: `MoveSystem` moves everything with a velocity, `ReportSystem` prints
the players (`[With<Player>]`), `ClockSystem` advances the clock. The systems are stateless; every value lives in a
component or the singleton.

```bash
dotnet run --project samples/HelloWorld
```

Expected output:

```
tick 0: player at (1, 2)
tick 1: player at (2, 4)
tick 2: player at (3, 6)
2 entities after 3 ticks
```
