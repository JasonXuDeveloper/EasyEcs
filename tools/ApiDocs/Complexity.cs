using System.Text.RegularExpressions;

namespace ApiDocs;

/// <summary>
/// The complexity column of the API table (docs/content/api.md): for each row, the calls of its first cell
/// (<c>world.CreateEntity(in T1 … in Tn)</c>) by type and member, with the row's complexity.
/// </summary>
public static partial class Complexity
{
    // How the table names the receiver of a call.
    private static readonly Dictionary<string, string[]> Receivers = new()
    {
        ["world"] = ["World"], ["World"] = ["World"], ["entity"] = ["Entity"], ["prefab"] = ["Prefab"],
        ["bag"] = ["DynamicBuffer", "DynamicBufferExtensions"], ["index"] = ["ValueIndex", "OrderedIndexExtensions"],
        ["filter"] = ["QueryFilter"], ["QueryFilter"] = ["QueryFilter"], ["system"] = ["EcsSystem"],
        ["snapshot"] = ["WorldSnapshot"], ["singletonRef"] = ["SingletonRef"],
    };

    /// <summary>(type name, member name) to the table's rows for it: (the call as the table writes it, complexity).</summary>
    public static Dictionary<(string Type, string Member), List<(string Call, string Cost)>> Load(string root)
    {
        var table = new Dictionary<(string, string), List<(string, string)>>();
        var path = new[] { "api.mdx", "api.md" }.Select(f => Path.Combine(root, "docs", "content", f)).FirstOrDefault(File.Exists);
        if (path == null) return table;
        int column = -1;
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith('|'))
            {
                column = -1;
                continue;
            }

            var cells = Cells(line);
            if (column < 0)
            {
                column = cells.FindIndex(c => c.Trim() == "Complexity"); // a header row
                continue;
            }

            if (column >= cells.Count || cells[0].Trim().StartsWith("---", StringComparison.Ordinal)) continue;
            string cost = Relative().Replace(cells[column].Trim(), "](/en/docs/$1$2)");
            string? receiver = null;
            foreach (Match span in Spans().Matches(cells[0]))
            {
                var call = Call().Match(span.Groups[1].Value);
                if (!call.Success) continue;
                string member = call.Groups["member"].Value;
                if (call.Groups["new"].Success) (receiver, member) = (member, ".ctor");
                else if (call.Groups["receiver"].Success) receiver = call.Groups["receiver"].Value;
                if (receiver == null || !Receivers.TryGetValue(receiver, out var types)) continue;
                foreach (var type in types)
                {
                    if (!table.TryGetValue((type, member), out var rows)) table[(type, member)] = rows = [];
                    if (!rows.Contains((span.Value, cost))) rows.Add((span.Value, cost));
                }
            }
        }

        return table;
    }

    // Cells of a table row, keeping pipes inside code spans.
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool code = false;
        for (int i = 1; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '`') code = !code;
            if (c == '|' && !code && (i == 0 || line[i - 1] != '\\'))
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else cell.Append(c);
        }

        return cells;
    }

    // A link relative to the API page (guide/change-tracking.md#ticks), as a site URL.
    [GeneratedRegex(@"\]\((?!https?:|/|#)([^)#]*?)\.mdx?(#[^)]*)?\)")]
    private static partial Regex Relative();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Spans();

    // `world.CreateEntity(...)`, `GetEntity(id)` (the receiver of the cell's previous call), `new World(options)`.
    [GeneratedRegex(@"^(?:(?<new>new )|(?<receiver>\w+)\.)?(?<member>[A-Z]\w*)")]
    private static partial Regex Call();
}
