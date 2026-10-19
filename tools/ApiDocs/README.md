# ApiDocs

Generates the API reference of the website (`docs/content/reference/`) from EasyEcs.Core's public API and its XML
documentation comments (`GenerateDocumentationFile` in `EasyEcs.Core.csproj`), in English and in Chinese.

```bash
dotnet run --project tools/ApiDocs                    # writes docs/content/reference/
dotnet run --project tools/ApiDocs -- /tmp/ref        # or another directory
dotnet run --project tools/ApiDocs -- --zh-scaffold   # adds the members without a translation to tools/ApiDocs/zh/
```

One MDX page per public type (a `T1..T9` family is one page), plus `index.mdx` (types by kind) and `meta.json`, each
also as `<page>.zh.mdx` / `meta.zh.json`. Each page has frontmatter (`title`, `seoTitle`, `description` from the
type's summary), the declaration, and per member: signature, summary, type parameters, parameters, returns, remarks,
exceptions and complexity. Complexity comes from the Complexity column of the API tables (`docs/content/api.mdx`,
`api.zh.mdx` for the Chinese pages). Members hidden with `[EditorBrowsable(Never)]` are left out.

## Chinese pages

The source code stays English. The Chinese text is in `zh/<page>.xml`, one `<member>` per documentation id, with the
same elements as the English XML doc (`summary`, `param`, `returns`, `remarks`, `exception`...) and the same markup
(`<see cref>`, `<c>`, `<code>`, `<para>`, `<br/>`), so links and code render the same way. `source` is a hash of the
English text the entry translates. Signatures, headings and generated sentences come from the tool (`Language.cs`);
links point to `/zh/docs/...`. Two members with the same English text share one entry (a positional record's type and
its constructor). How to write the text and the terms to use: [`zh/README.md`](zh/README.md).

The tool reports every problem and exits with 1, which fails CI:

- a member shown on a page with English docs and no Chinese entry (`--zh-scaffold` adds it with the English text, still
  reported until it holds Chinese);
- an entry whose English changed since it was translated (`source` differs: the message prints the current English;
  update the Chinese, then set `source` to the hash it names);
- an entry whose elements differ from the English ones (a parameter added, renamed or removed);
- an entry for a member that is no longer documented or shown (delete it).

Until it is fixed, a member without a usable translation shows its English text on the Chinese page.

The output is deterministic, and the pages are committed: CI regenerates them and fails if they differ. After changing
a public API or its doc comment, update its Chinese entry, run the tool and commit the pages with the change. Do not
edit the pages by hand.
