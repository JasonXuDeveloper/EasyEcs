namespace DocCheck;

/// <summary>A fenced code block: its page, the line of its first code line (1-based), its language, meta and code.</summary>
public sealed record Fence(string Page, int Line, string Language, string Meta, string Code)
{
    /// <summary>The <c>doccheck=</c> options of the meta (<c>doccheck=skip</c>, <c>doccheck=alone,expect-EECS007</c>).</summary>
    public IReadOnlyList<string> Options { get; } = ParseOptions(Meta);

    public bool Skip => Options.Contains("skip");

    /// <summary>Compiled on its own, without the page's other fences.</summary>
    public bool Alone => Options.Contains("alone");

    /// <summary>
    /// Part of the page's full example: the <c>example</c> fences of a page (its Components.cs, Systems.cs and
    /// Program.cs) compile together, apart from the page's other fences.
    /// </summary>
    public bool Example => Options.Contains("example");

    /// <summary>A fragment of a system's body (<c>[ForEach]</c> methods, overrides): compiled inside an <c>EcsSystem</c>.</summary>
    public bool InSystem => Options.Contains("system");

    /// <summary>The diagnostics this fence must report (a bad example): <c>expect-EECS007</c>.</summary>
    public IEnumerable<string> Expected => Options.Where(o => o.StartsWith("expect-", StringComparison.Ordinal)).Select(o => o["expect-".Length..]);

    public int LastLine => Line + Code.Count(c => c == '\n');

    private static string[] ParseOptions(string meta)
    {
        foreach (var token in meta.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("doccheck=", StringComparison.Ordinal))
                return token["doccheck=".Length..].Trim('"', '\'').Split(',', StringSplitOptions.RemoveEmptyEntries);
        return [];
    }
}

/// <summary>Reads the fenced code blocks of a Markdown or MDX page (CommonMark fences: ``` or ~~~, indented in lists).</summary>
public static class Markdown
{
    public static readonly string[] CSharp = ["csharp", "cs", "c#"];

    public static List<Fence> Fences(string page, string text)
    {
        var fences = new List<Fence>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Opens(lines[i], out int indent, out char marker, out int length, out string info)) continue;
            int first = i + 1;
            int end = first;
            while (end < lines.Length && !Closes(lines[end], marker, length)) end++;
            var code = new System.Text.StringBuilder();
            for (int k = first; k < end; k++) code.Append(Unindent(lines[k], indent)).Append('\n');
            int space = info.IndexOfAny([' ', '\t']);
            string language = space < 0 ? info : info[..space];
            string meta = space < 0 ? "" : info[(space + 1)..].Trim();
            fences.Add(new Fence(page, first + 1, language.ToLowerInvariant(), meta, code.ToString()));
            i = end;
        }

        return fences;
    }

    private static bool Opens(string line, out int indent, out char marker, out int length, out string info)
    {
        indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        marker = indent < line.Length ? line[indent] : '\0';
        length = 0;
        info = "";
        if (marker is not ('`' or '~')) return false;
        while (indent + length < line.Length && line[indent + length] == marker) length++;
        if (length < 3) return false;
        info = line[(indent + length)..].Trim();
        return marker != '`' || !info.Contains('`');
    }

    private static bool Closes(string line, char marker, int length)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= length && trimmed.All(c => c == marker);
    }

    private static string Unindent(string line, int indent)
    {
        int strip = 0;
        while (strip < indent && strip < line.Length && line[strip] == ' ') strip++;
        return line[strip..];
    }
}
