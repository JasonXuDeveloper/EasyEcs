# EasyEcs.Templates

`dotnet new` templates, published to NuGet as `EasyEcs.Templates` with each release (same version as `EasyEcs`).

```bash
dotnet new install EasyEcs.Templates
dotnet new easyecs -n MyServer              # a console server: a world updated at 20 Hz until Ctrl+C
dotnet new easyecs -n MyServer --tests      # plus MyServer.Tests (NUnit) with a test of a system
cd MyServer && dotnet new easyecs-system -n RegenSystem --namespace MyServer   # one system file
dotnet run -- --ticks 100                   # stop after 100 ticks
```

The `easyecs` project has components and a singleton (`Components.cs`), three systems (`Systems.cs`) and a fixed-tick
loop that spawns a wave of entities from a prefab every second (`Program.cs`). The test project shows how a system is
tested: a world of its own, the entities the system needs, one update, assertions on the data.

`content/` holds the templates; `EASYECS_VERSION` in it becomes the version of `Directory.Build.props` when the
package is packed. `templates/smoke.sh` packs EasyEcs and the templates into a local feed, creates both templates and
builds, runs and tests the result (CI runs it).
