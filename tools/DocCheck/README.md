# DocCheck

Compiles every C# code block of the docs, so a snippet that no longer matches the API, or that the analyzer would
flag, fails CI with the page and line.

```bash
dotnet run --project tools/DocCheck                              # README.md and docs/content/**/*.md(x)
dotnet run --project tools/DocCheck -- docs/content/faq.md        # some pages
dotnet run --project tools/DocCheck -- --dump /tmp/dc docs/content/faq.md   # also write the generated sources
```

Output: a summary per page, then one line per problem, `docs/content/faq.md:42: error CS0103: The name 'x' does not
exist in the current context` (and a `::error` annotation on GitHub Actions). The exit code is 1 if there is any
problem. The generated API reference (`docs/content/reference/`) is not checked: its blocks are signatures.

A fence is C# if its language is `csharp`, `cs` or `c#`. Other languages are ignored.

## How a page compiles

All the C# fences of a page compile together, in order, as if they were one program, against EasyEcs.Core with its
generator and analyzer. A page reads like a story, and DocCheck compiles the story:

- **Types** (`record struct Position(...) : IComponent`, systems, enums) are declarations, as written.
- **A later declaration replaces an earlier one** of the same name. A page may show a type, then a better version of
  it; the later one is used everywhere (partial types are kept together).
- **Members** (a field or method with a modifier: `static void Move(...)`, `private int _count;`) go into one class
  that every fence sees.
- **Statements** (`var hero = world.CreateEntity(...)`; `world.Update();`) become the body of a method, one per fence.
  `await` makes it async; `return` is allowed.
- **Locals carry over**: a local declared in one fence can be used by a later fence without declaring it again.
- **Ambient names** may be used undeclared: `world` (a `World`), `entity`, `unit`, `hero`, `player` (an `Entity`),
  `running` (a `bool`) and `args` (a `string[]`, the command line of a top-level `Program.cs`).
- **The prelude** (`Prelude.cs`) declares components the docs share: `Position(float X, float Y)`,
  `Velocity(float X, float Y)`, `Health(int Value)`, `Burning(int Ticks)` and the tags `Dead`, `Frozen`, `Stunned`.
  A page that declares one of these names uses its own.
- **Implicit usings**: `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` and `EasyEcs`. A fence's own `using` lines apply to the page.
- A fence with a `namespace` is a file of its own.
- **What fails**: every compiler error, and every analyzer finding (`EECS…`, warnings included). Compiler warnings
  (unused variables and the like) do not, nor does the analyzer's suggestion (Info: EECS033) unless a fence expects
  one (`expect-EECS033`).

## Flags

Flags go after the language, as `doccheck=` with comma-separated values. MDX and Markdown renderers ignore them:

````md
```csharp doccheck=skip
```
````

| Flag | Meaning |
|---|---|
| `skip` | not compiled (pseudo-code, a fragment that cannot be made to compile, another language in a `csharp` fence) |
| `alone` | compiled on its own, without the page's other fences (a self-contained example that would clash with them) |
| `example` | part of the page's full example: every `example` fence of the page (its `Components.cs`, `Systems.cs`, `Program.cs`) compiles together, as one program apart from the other fences, so its types never replace theirs |
| `system` | the fence is a fragment of a system (`[ForEach]` methods, an `OnUpdate` override): compiled inside an `EcsSystem` |
| `expect-ID` | a bad example: the fence must report diagnostic `ID` (`expect-EECS007`, `expect-CS0029`) and nothing else; it compiles on its own with the page's declarations |

Flags combine: `doccheck=system,expect-EECS003`.

## Writing snippets that compile

- Declare what the snippet uses, or use the prelude and the ambient names. A snippet that needs a component the
  reader can guess from its name still has to declare it once on the page, in an earlier fence if the snippet
  itself should stay short.
- Bad examples (what the analyzer rejects) carry `expect-` with the diagnostic: the check then proves the analyzer
  really reports it.
- Use `skip` sparingly: a skipped snippet can go stale.
