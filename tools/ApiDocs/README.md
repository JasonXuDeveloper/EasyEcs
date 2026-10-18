# ApiDocs

Generates the API reference of the website (`docs/content/reference/`) from EasyEcs.Core's public API and its XML
documentation comments (`GenerateDocumentationFile` in `EasyEcs.Core.csproj`).

```bash
dotnet run --project tools/ApiDocs               # writes docs/content/reference/
dotnet run --project tools/ApiDocs -- /tmp/ref   # or another directory
```

One MDX page per public type (a `T1..T9` family is one page), plus `index.mdx` (types by kind) and `meta.json`. Each
page has frontmatter (`title`, `description` from the type's summary), the declaration, and per member: signature,
summary, type parameters, parameters, returns, remarks, exceptions and complexity. Complexity comes from the
Complexity column of the API table (`docs/content/api.md`). Members hidden with `[EditorBrowsable(Never)]` are left out.
The pages are English only.

The output is deterministic, and the pages are committed: CI regenerates them and fails if they differ. After changing
a public API or its doc comment, run the tool and commit the pages with the change. Do not edit the pages by hand.
