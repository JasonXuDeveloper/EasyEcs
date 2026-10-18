using DocCheck;

// dotnet run --project tools/DocCheck [-- [--dump dir] page.md ...]
// Compiles the C# fences of README.md and docs/content/**/*.md(x) (or of the pages given) except the generated API
// reference (docs/content/reference: signatures from tools/ApiDocs, not snippets); prints page:line for each
// problem and exits 1 if there is one. --dump writes the generated sources of each compilation into dir.
// README.md describes the conventions.
var root = Root();
string? dump = null;
var given = new List<string>();
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--dump" && i + 1 < args.Length) dump = Path.GetFullPath(args[++i]);
    else given.Add(Path.GetFullPath(args[i]));
var reference = Path.Combine(root, "docs", "content", "reference") + Path.DirectorySeparatorChar;
var pages = given.Count != 0
    ? given
    : [Path.Combine(root, "README.md"), .. Directory.EnumerateFiles(Path.Combine(root, "docs", "content"), "*.*", SearchOption.AllDirectories)
        .Where(p => (p.EndsWith(".md", StringComparison.Ordinal) || p.EndsWith(".mdx", StringComparison.Ordinal)) && !p.StartsWith(reference, StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)];

var compiler = new PageCompiler { DumpTo = dump };
bool annotate = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
int total = 0, checkedFences = 0;
foreach (var path in pages)
{
    string page = Path.GetRelativePath(root, path).Replace('\\', '/');
    var fences = Markdown.Fences(page, File.ReadAllText(path));
    var csharp = fences.Where(f => Markdown.CSharp.Contains(f.Language)).ToList();
    if (csharp.Count == 0) continue;
    var failures = await compiler.CheckAsync(page, fences);
    checkedFences += csharp.Count(f => !f.Skip);
    total += failures.Count;
    Console.WriteLine($"{page}: {csharp.Count} fences ({csharp.Count(f => f.Skip)} skipped), {failures.Count} problems");
    foreach (var failure in failures.OrderBy(f => f.Line))
    {
        Console.WriteLine($"  {failure.Page}:{failure.Line}: {failure.Message}");
        if (annotate) Console.WriteLine($"::error file={failure.Page},line={failure.Line}::{failure.Message}");
    }
}

Console.WriteLine($"{checkedFences} fences compiled, {total} problems");
return total == 0 ? 0 : 1;

static string Root()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "EasyEcs.slnx")))
            return dir.FullName;
    throw new InvalidOperationException("Run inside the EasyEcs repository (no EasyEcs.slnx above the current directory)");
}
