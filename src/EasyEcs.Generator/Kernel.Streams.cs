using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EasyEcs.Generator;

// The columns a vector loop writes without reading them, which the loop streams past the last-level cache
// (EasyEcs.StreamingStores, microarch.md candidate 8): read off the loop's own text, so whatever the translation wrote
// decides, never a second reading of the method.
internal sealed partial class Kernel
{
    /// <summary>
    /// The columns of <paramref name="written"/> whose values the loop body <paramref name="body"/> never reads: every
    /// vector of the column is loaded by one line (<c>var __c1_0 = V.LoadUnsafe(ref e1, ...)</c>, a pair's halves
    /// <c>__c1_0_0</c> / <c>__c1_0_1</c> each), and the first later line naming it assigns it a value that does not name
    /// it, in a body without blocks (so that line runs for every lane: a write under a condition selects the old lanes
    /// back, <c>ConditionalSelect(m, new, __c1_0)</c>, which reads them). A column of several fields loads through
    /// <c>Interleaved.Load2</c>-<c>4</c> and is never one of them (its stores interleave: a non-temporal form of them
    /// would be the next step), nor a bool column (its load is a compare).
    /// </summary>
    internal static int[] WriteOnly(string body, IEnumerable<int> written)
    {
        string[] lines = body.Split('\n');
        if (lines.Any(l => l.StartsWith("if ", System.StringComparison.Ordinal) || l.StartsWith("for ", System.StringComparison.Ordinal)
            || l.StartsWith("{", System.StringComparison.Ordinal) || l.StartsWith("}", System.StringComparison.Ordinal)
            || l.StartsWith("else", System.StringComparison.Ordinal) || l.StartsWith("while ", System.StringComparison.Ordinal)))
            return [];
        var only = new List<int>();
        foreach (int k in written.OrderBy(k => k))
        {
            var load = new Regex($@"^var (__c{k}_0(?:_[01])?) = {Regex.Escape(V)}\.LoadUnsafe\(ref e{k}, [^\n]*\);$");
            var names = new List<(string Name, int Line)>();
            bool other = false;
            for (int l = 0; l < lines.Length; l++)
            {
                if (load.Match(lines[l]) is { Success: true } m) names.Add((m.Groups[1].Value, l));
                else if (Regex.IsMatch(lines[l], $@"\be{k}\b") && !lines[l].Contains($"StoreUnsafe(")) other = true; // a load of another form
            }
            if (names.Count == 0 || other) continue;
            if (names.All(n => AssignedFirst(lines, n.Name, n.Line))) only.Add(k);
        }
        return only.ToArray();
    }

    /// <summary>Whether the first line after <paramref name="from"/> naming <paramref name="name"/> assigns it without reading it.</summary>
    private static bool AssignedFirst(string[] lines, string name, int from)
    {
        var named = new Regex($@"\b{name}\b");
        for (int l = from + 1; l < lines.Length; l++)
        {
            if (!named.IsMatch(lines[l])) continue;
            string assigned = name + " = ";
            return lines[l].StartsWith(assigned, System.StringComparison.Ordinal) && !named.IsMatch(lines[l].Substring(assigned.Length));
        }
        return false; // never written: not stored either
    }

    /// <summary>
    /// The text of a streaming loop (<paramref name="body"/> and <paramref name="stores"/> of a vector or flat loop):
    /// each column of <paramref name="streamed"/> declared without its load (<c>default(V&lt;T&gt;)</c>, lane type
    /// <paramref name="columns"/>[k - 1]: a dead load would still read the line, since the JIT keeps a load that may
    /// fault) and stored through <c>StreamingStores.Store</c>.
    /// </summary>
    internal static (string Body, string Stores) Streaming(string body, string stores, EquatableArray<string> columns, EquatableArray<int> streamed)
    {
        foreach (int k in streamed)
        {
            body = Regex.Replace(body, $@"(?m)^var (__c{k}_0(?:_[01])?) = {Regex.Escape(V)}\.LoadUnsafe\(ref e{k}, [^\n]*\);$",
                $"var $1 = default({V.Replace("$", "$$")}<{columns[k - 1]}>);");
            stores = Regex.Replace(stores, $@"(?m)^{Regex.Escape(V)}\.StoreUnsafe\((.*), ref e{k}, ([^\n]*)\);$",
                $"global::EasyEcs.StreamingStores.Store($1, ref e{k}, $2);");
        }
        return (body, stores);
    }
}
