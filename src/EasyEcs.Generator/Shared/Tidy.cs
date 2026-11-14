using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyEcs.Generator;

/// <summary>
/// The last passes over a generated file's text (<see cref="Code.Aliased"/>), which make it read like hand-written code
/// and leave its IL as it was: the other qualified type names the file repeats through aliases (<see cref="Types"/>),
/// and the parentheses precedence does not need dropped (<see cref="Parentheses"/>). Both read the text as tokens, skip
/// comments, string and character literals, using directives and preprocessor lines, and change only what they can
/// prove: a name they cannot tell is a type, or a parenthesis whose neighbors they do not know, stays as written.
/// </summary>
internal static class Tidy
{
    // ---------------- aliases ----------------

    /// <summary>The attributes generated code names (an attribute's alias names its class, which ends in Attribute).</summary>
    private static readonly string[] Attributes =
    [
        "global::System.ComponentModel.EditorBrowsable", "global::System.Runtime.CompilerServices.UnsafeAccessor",
        "global::System.Runtime.CompilerServices.UnsafeAccessorType", "global::System.Runtime.CompilerServices.InterceptsLocation",
        "global::System.CodeDom.Compiler.GeneratedCode", "global::System.Runtime.CompilerServices.ModuleInitializer",
        "global::System.AttributeUsage", "global::System.Runtime.CompilerServices.SkipLocalsInit",
    ];

    /// <summary>Types generated code names only as the left side of a member (an enum's values in attribute arguments).</summary>
    private static readonly string[] Seeds =
    [
        "global::System.ComponentModel.EditorBrowsableState", "global::System.Runtime.CompilerServices.UnsafeAccessorKind",
        "global::System.AttributeTargets",
    ];

    /// <summary>Words that follow an expression, not a type's name (<c>global::E.A or global::E.B</c>).</summary>
    private static readonly string[] AfterExpression =
    [
        "and", "or", "not", "is", "as", "when", "switch", "with", "in", "into", "on", "equals", "by", "ascending", "descending",
    ];

    /// <summary>A type declaration's head or a constraint, whose names after the colon are types.</summary>
    private static readonly Regex Declaration = new(
        @"\G\s*(?:(?:public|internal|private|protected|static|readonly|ref|partial|sealed|abstract|unsafe|file|new)\s+)*(?:class|struct|interface|record)\b|\G\s*where\s",
        RegexOptions.CultureInvariant);

    /// <summary>A qualified name: where it starts, where its dotted identifiers end, and where its type arguments end (-1: none).</summary>
    private readonly record struct Name(int Start, int End, int GenericEnd);

    /// <summary>
    /// <paramref name="text"/> with the qualified type names (<c>global::EasyEcs.Benchmark.KernelBenchmark.Heat</c>) it
    /// names twice or more written through a file-level alias (<c>__Heat</c>), added to <paramref name="aliases"/> (alias
    /// to target, which already holds the names <see cref="Code.Aliased"/>'s BCL pass gave). A name is a type where its
    /// position says so: declared (<c>global::X x</c>), a type argument, created, the type of a member called or read
    /// (<c>global::X.M(</c>), a base, or an attribute; a type with its arguments (<c>IForEachBatch&lt;A, B&gt;</c>) gets
    /// one alias of its own when its arguments are all qualified. An alias of a namespace taken for a type would still
    /// be right: only a name of a member must never get one, and a member is never in those positions.
    /// </summary>
    public static List<(int Start, int End, string Alias)> Types(string text, SortedDictionary<string, string> aliases)
    {
        var edits = new List<(int Start, int End, string Alias)>();
        var names = new List<Name>();
        var spans = new Spans(text);
        var types = new HashSet<Span>(spans);
        var closed = new Dictionary<Span, int>(spans);
        var reserved = new HashSet<string>(aliases.Keys, StringComparer.Ordinal); // and every __ name of the text (Used)
        var existing = new Dictionary<string, string>(StringComparer.Ordinal); // target -> alias written by the template
        foreach (var pair in aliases) existing[pair.Value] = pair.Key;
        // the template's own alias directives (using __ForEach = ...;)
        for (int u = text.StartsWith("using __", StringComparison.Ordinal) ? 0 : text.IndexOf("\nusing __", StringComparison.Ordinal); u >= 0;
             u = text.IndexOf("\nusing __", u + 1, StringComparison.Ordinal))
        {
            int from = text[u] == '\n' ? u + 1 : u, to = text.IndexOf('\n', from);
            Existing(text, from, to < 0 ? text.Length : to, existing, reserved);
        }
        // each global:: name in code (IndexOf finds them; a line's comment, string or directive leaves one out)
        for (int i = text.IndexOf("global::", StringComparison.Ordinal); i >= 0; i = text.IndexOf("global::", i + 8, StringComparison.Ordinal))
        {
            if (i > 0 && (IsPart(text[i - 1]) || text[i - 1] == '.') || !InCode(text, i) || Parse(text, i) is not { } name) continue;
            names.Add(name);
            Classify(text, name, types, closed);
        }
        if (names.Count == 0) return edits;

        // What each name becomes: a closed generic's alias, else its longest prefix known as a type.
        var targets = new List<(int Start, int End, Span Target, bool Attribute)>();
        var uses = new Dictionary<(Span, bool), int>(new SpansOrAttribute(spans));
        int skipUntil = 0;
        foreach (var name in names)
        {
            if (name.Start < skipUntil) continue;
            Span? target = null;
            bool attribute = false;
            int end = 0;
            if (name.GenericEnd > 0 && Usage(text, name) && new Span(name.Start, name.GenericEnd - name.Start) is var whole
                && closed.TryGetValue(whole, out int count) && (count >= 2 || existing.ContainsKey(spans.Text(whole))) && Qualified(text, name.End + 1, name.GenericEnd - 1))
            {
                target = whole;
                end = name.GenericEnd;
                skipUntil = end;
            }
            else
            {
                if (name.GenericEnd < 0 && IsAttribute(text, name) && IsAny(text, name.Start, name.End, Attributes))
                {
                    target = new Span(name.Start, name.End - name.Start);
                    attribute = true;
                    end = name.End;
                }
                else
                    for (int k = name.End; k > name.Start + 8; k = text.LastIndexOf('.', k - 1, k - 1 - (name.Start + 8)))
                    {
                        if (k == name.End && name.GenericEnd > 0) continue; // a generic type is not its non-generic namesake
                        if (k < 0) break;
                        var prefix = new Span(name.Start, k - name.Start);
                        if (!types.Contains(prefix) && !IsAny(text, name.Start, k, Seeds)) continue;
                        target = prefix;
                        end = k;
                        break;
                    }
            }
            if (target is not { } t) continue;
            targets.Add((name.Start, end, t, attribute));
            uses[(t, attribute)] = uses.TryGetValue((t, attribute), out int u) ? u + 1 : 1;
        }

        // An alias for each target named twice or more (or that already has one); names the file does not use. The
        // targets' text is copied only here, once each.
        var alias = new Dictionary<(Span, bool), string>(uses.Comparer);
        HashSet<int>? named = null;
        foreach (var (key, count, target) in uses.Select(p => (p.Key, p.Value, spans.Text(p.Key.Item1) + (p.Key.Item2 ? "Attribute" : "")))
                     .OrderBy(p => p.Item3, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(target, out var had))
            {
                alias[key] = had;
                continue;
            }
            if (count < 2) continue;
            string chosen = AliasOf(target, reserved, text, named ??= Named(text));
            reserved.Add(chosen);
            alias[key] = chosen;
            aliases[chosen] = target;
        }
        int at = 0;
        foreach (var (start, end, target, attribute) in targets)
        {
            if (!alias.TryGetValue((target, attribute), out var a) || start < at) continue;
            edits.Add((start, end, a));
            at = end;
        }
        return edits;
    }

    /// <summary>A range of the file's text: a name compared by its characters, copied only when it becomes an alias's target.</summary>
    private readonly record struct Span(int Start, int Length);

    /// <summary>Equality of <see cref="Span"/>s of one text by their characters.</summary>
    private sealed class Spans(string text) : IEqualityComparer<Span>
    {
        public string Text(Span s) => text.Substring(s.Start, s.Length);

        public bool Equals(Span a, Span b) => a.Length == b.Length && string.CompareOrdinal(text, a.Start, text, b.Start, a.Length) == 0;

        public int GetHashCode(Span s) => Hash(text, s.Start, s.Length);
    }

    /// <summary><see cref="Spans"/> with the attribute flag (an attribute's target is its class, the name plus "Attribute").</summary>
    private sealed class SpansOrAttribute(Spans spans) : IEqualityComparer<(Span, bool)>
    {
        public bool Equals((Span, bool) a, (Span, bool) b) => a.Item2 == b.Item2 && spans.Equals(a.Item1, b.Item1);

        public int GetHashCode((Span, bool) s) => spans.GetHashCode(s.Item1) ^ (s.Item2 ? 1 : 0);
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="edits"/> (ranges written as another text, in order) applied and the
    /// characters at <paramref name="dropped"/> (in order) left out, from <paramref name="from"/> on, appended to
    /// <paramref name="sb"/>: one copy of the file for both passes.
    /// </summary>
    public static void Write(StringBuilder sb, string text, int from, List<(int Start, int End, string Alias)> edits, List<int> dropped)
    {
        int at = from, e = 0, d = 0;
        while (e < edits.Count || d < dropped.Count)
        {
            bool edit = e < edits.Count && (d >= dropped.Count || edits[e].Start < dropped[d]);
            if (edit)
            {
                var (start, end, alias) = edits[e++];
                if (start < at) continue;
                sb.Append(text, at, start - at).Append(alias);
                at = end;
            }
            else
            {
                int k = dropped[d++];
                if (k < at) continue; // inside an edit's range
                sb.Append(text, at, k - at);
                at = k + 1;
            }
        }
        sb.Append(text, at, text.Length - at);
    }

    /// <summary>
    /// Whether <paramref name="at"/> is code: not in a using directive or a preprocessor line, nor after a line comment or
    /// inside a string or character literal of its line. Generated code puts no block comment or multi-line literal
    /// around a qualified name, so the line alone decides.
    /// </summary>
    private static bool InCode(string text, int at)
    {
        int k = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        while (k < at && text[k] is ' ' or '\t' or '\uFEFF') k++;
        if (k < at && text[k] == '#' || string.CompareOrdinal(text, k, "using ", 0, 6) == 0) return false;
        for (; k < at; k++)
        {
            char c = text[k];
            if (c == '/' && k + 1 < at && text[k + 1] == '/') return false;
            if (c is '"' or '\'')
            {
                int end = SkipLiteral(text, k);
                if (end > at) return false;
                k = end - 1;
            }
        }
        return true;
    }

    /// <summary>The name of a qualified name at <paramref name="i"/> (its <c>global::</c>), or null when no identifier follows.</summary>
    private static Name? Parse(string text, int i)
    {
        int j = i + 8;
        if (j >= text.Length || !IsStart(text[j])) return null;
        j = IdentifierEnd(text, j);
        while (j + 1 < text.Length && text[j] == '.' && IsStart(text[j + 1])) j = IdentifierEnd(text, j + 1);
        return new Name(i, j, j < text.Length && text[j] == '<' ? GenericEnd(text, j) : -1);
    }

    /// <summary>Records what <paramref name="name"/>'s position proves: the name or its left side a type, or a closed generic type.</summary>
    private static void Classify(string text, Name name, HashSet<Span> types, Dictionary<Span, int> closed)
    {
        int after = name.GenericEnd > 0 ? name.GenericEnd : name.End;
        char next = after < text.Length ? text[after] : '\0';
        bool afterNew = WordBefore(text, name.Start, "new", out char prev);
        if (name.GenericEnd > 0)
        {
            if (next == '(' && !afterNew) Left(text, name, types); // a generic method's type
            else
            {
                var whole = new Span(name.Start, name.GenericEnd - name.Start);
                closed[whole] = closed.TryGetValue(whole, out int c) ? c + 1 : 1;
            }
            return;
        }
        var chain = new Span(name.Start, name.End - name.Start);
        if (next == '(')
        {
            if (afterNew) types.Add(chain);
            else if (!IsAttribute(text, name)) Left(text, name, types); // a member called: its left side is a type
            return;
        }
        if (IsAttribute(text, name)) return;
        bool type = next == '>' || name.Start > 0 && text[name.Start - 1] == '<'
            || next == '[' && after + 1 < text.Length && text[after + 1] == ']'
            || prev == '(' && (WordBefore(text, name.Start, "typeof", out _) || WordBefore(text, name.Start, "default", out _) || WordBefore(text, name.Start, "sizeof", out _));
        if (!type && next == ' ' && after + 1 < text.Length && IsStart(text[after + 1]))
            type = !IsAny(text, after + 1, IdentifierEnd(text, after + 1), AfterExpression);
        if (!type && next is ',' or '\n' or '\r' or ' ' or '{')
        {
            int line = text.LastIndexOf('\n', name.Start) + 1;
            type = text.IndexOf(" : ", line, name.Start - line, StringComparison.Ordinal) >= 0 && Declaration.IsMatch(text, line);
        }
        if (type) types.Add(chain);
    }

    /// <summary>The left side of <paramref name="name"/>'s last dot, a type (the name is a member of it).</summary>
    private static void Left(string text, Name name, HashSet<Span> types)
    {
        int dot = text.LastIndexOf('.', name.End - 1, name.End - name.Start - 8);
        if (dot > name.Start + 8) types.Add(new Span(name.Start, dot - name.Start));
    }

    /// <summary>Whether the closed generic <paramref name="name"/> is used as a type, not called as a generic method.</summary>
    private static bool Usage(string text, Name name) =>
        !(name.GenericEnd < text.Length && text[name.GenericEnd] == '(' && !WordBefore(text, name.Start, "new", out _));

    /// <summary>Whether <paramref name="name"/> names an attribute (<c>[global::X(</c>, <c>[global::X]</c>).</summary>
    private static bool IsAttribute(string text, Name name) => name.Start > 0 && text[name.Start - 1] == '[';

    /// <summary>
    /// Whether the type arguments in <paramref name="text"/>[<paramref name="from"/>, <paramref name="to"/>) are all
    /// qualified names or keywords, so a using directive can name them (it sees no other alias, nor a type parameter).
    /// </summary>
    private static bool Qualified(string text, int from, int to)
    {
        for (int i = from; i < to;)
        {
            if (!IsStart(text[i]))
            {
                i++;
                continue;
            }
            int e = IdentifierEnd(text, i);
            if (e - i == 6 && string.CompareOrdinal(text, i, "global", 0, 6) == 0 && e + 1 < to && text[e] == ':')
            {
                i = e + 2;
                // the dotted identifiers after it
                while (i < to && (IsPart(text[i]) || text[i] == '.')) i++;
                continue;
            }
            if (!IsAny(text, i, e, Keywords)) return false;
            i = e;
        }
        return true;
    }

    private static readonly string[] Keywords =
    [
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "float", "double", "decimal", "char",
        "string", "object",
    ];

    /// <summary>Whether <paramref name="text"/>[<paramref name="from"/>, <paramref name="to"/>) is one of <paramref name="words"/>.</summary>
    private static bool IsAny(string text, int from, int to, string[] words)
    {
        foreach (var word in words)
            if (word.Length == to - from && string.CompareOrdinal(text, from, word, 0, word.Length) == 0) return true;
        return false;
    }

    /// <summary>An alias for <paramref name="target"/> the file does not use: <c>__Heat</c>, else with its outer name or arguments, else numbered.</summary>
    private static string AliasOf(string target, HashSet<string> reserved, string text, HashSet<int> named)
    {
        bool Taken(string name) => reserved.Contains(name) || named.Contains(Hash(name, 0, name.Length)) && Used(text, name);
        int open = target.IndexOf('<');
        string chain = open < 0 ? target : target.Substring(0, open);
        string[] segments = chain.Substring(8).Split('.');
        string simple = segments[segments.Length - 1].TrimStart('@');
        if (simple.EndsWith("Attribute", StringComparison.Ordinal) && simple.Length > 9) simple = simple.Substring(0, simple.Length - 9);
        // a closed generic by its arguments too (__IForEachBatch_P3_V3), so two of one file read apart
        string first = open < 0 ? "__" + simple
            : "__" + simple + "_" + string.Join("_", Regex.Matches(target.Substring(open), @"[\w@]+(?=\s*[,>\[?])").Cast<Match>().Select(m => m.Value.TrimStart('@')));
        if (!Taken(first)) return first;
        string second = open < 0 && segments.Length > 1 ? "__" + segments[segments.Length - 2].TrimStart('@') + "_" + simple : first;
        if (!Taken(second)) return second;
        for (int k = 2; ; k++)
            if (!Taken(second + k)) return second + k;
    }

    /// <summary>The hashes of the <c>__</c> names <paramref name="text"/> holds: an alias candidate whose hash is not among them is free.</summary>
    private static HashSet<int> Named(string text)
    {
        var named = new HashSet<int>();
        for (int at = text.IndexOf("__", StringComparison.Ordinal); at >= 0; at = text.IndexOf("__", at + 2, StringComparison.Ordinal))
        {
            if (at > 0 && IsPart(text[at - 1])) continue;
            int end = IdentifierEnd(text, at);
            named.Add(Hash(text, at, end - at));
            at = end - 2;
        }
        return named;
    }

    /// <summary>FNV-1a over <paramref name="text"/>[<paramref name="from"/>, + <paramref name="length"/>).</summary>
    private static int Hash(string text, int from, int length)
    {
        unchecked
        {
            int hash = (int)2166136261;
            for (int i = from; i < from + length; i++) hash = (hash ^ text[i]) * 16777619;
            return hash;
        }
    }

    /// <summary>Whether <paramref name="text"/> names <paramref name="name"/> as a whole identifier.</summary>
    private static bool Used(string text, string name)
    {
        for (int at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
            if ((at == 0 || !IsPart(text[at - 1])) && (at + name.Length >= text.Length || !IsPart(text[at + name.Length]))) return true;
        return false;
    }

    /// <summary>The using alias directives the template wrote (<c>using __ForEach = global::...;</c>), between <paramref name="from"/> and <paramref name="to"/>.</summary>
    private static void Existing(string text, int from, int to, Dictionary<string, string> existing, HashSet<string> reserved)
    {
        var line = UsingAlias.Match(text.Substring(from, to - from));
        if (!line.Success) return;
        existing[line.Groups[2].Value] = line.Groups[1].Value;
        reserved.Add(line.Groups[1].Value);
    }

    private static readonly Regex UsingAlias = new(@"^\s*using\s+(\w+)\s*=\s*(.+?)\s*;", RegexOptions.CultureInvariant);

    // ---------------- parentheses ----------------

    /// <summary>The precedence of no binary operator: a primary or unary expression.</summary>
    private const int Primary = 100;

    /// <summary>
    /// <paramref name="text"/> without the parentheses precedence does not need: around a whole right-hand side or
    /// argument (<c>x = (a + b);</c>, <c>F((0))</c>), around an operand that binds tighter than its operator
    /// (<c>((a + b) - c)</c>, <c>a + (b * c)</c>), around a literal or name cast (<c>(byte)(2)</c>). The pairs are decided
    /// outermost first, each against its neighbors as they stand once the pairs around it are gone, so two pairs never
    /// both go where one must stay (<c>x * ((a + b))</c> keeps one). Kept: a tuple, a cast's own parentheses, a call's,
    /// a statement's (<c>if (...)</c>), a pair holding a lambda, an assignment, braces or a token sequence the scan does
    /// not know (<c>x is T</c>), a negative operand of a binary operator (<c>a - (-1)</c>), and any pair at a line's start
    /// or end.
    /// </summary>
    public static List<int> Parentheses(string text)
    {
        var pairs = new Pairs();
        var stack = new Stack<int>();
        int n = text.Length;
        for (int i = Preprocessor(text, 0); i < n;)
        {
            i = text.IndexOfAny(Brackets, i);
            if (i < 0) break;
            char c = text[i];
            if (c == '\n')
            {
                i = Preprocessor(text, i + 1);
                continue;
            }
            int s = SkipLiteral(text, i);
            if (s != i)
            {
                i = s;
                continue;
            }
            if (c is '(' or '[') stack.Push(pairs.Open(i));
            else if (c is ')' or ']' && stack.Count > 0) pairs.Close(stack.Pop(), i);
            i++;
        }
        for (int k = 0; k < pairs.Count; k++)
            if (text[pairs.OpenAt(k)] == '(' && pairs.CloseOf(k) is var close and > 0 && text[close] == ')' && Removable(text, pairs.OpenAt(k), close, pairs))
                pairs.Remove(k);
        var dropped = new List<int>();
        for (int k = 0; k < pairs.Count; k++)
            if (pairs.Removed(k))
            {
                dropped.Add(pairs.OpenAt(k));
                dropped.Add(pairs.CloseOf(k));
            }
        dropped.Sort();
        return dropped;
    }

    /// <summary>What the bracket scan stops at: brackets, the starts of literals and comments, a line's end (a directive may follow).</summary>
    private static readonly char[] Brackets = ['(', ')', '[', ']', '"', '\'', '/', '\n'];

    /// <summary>
    /// The text's bracket pairs, '(' and '[', in the order they open: where each closes, which are removed. Lookups by
    /// position are binary searches over the opens and the closes, both ascending as the scan meets them.
    /// </summary>
    private sealed class Pairs
    {
        private readonly List<int> _opens = [], _closeOf = [], _closes = [], _openOf = [];
        private readonly List<bool> _removed = [];

        public int Count => _opens.Count;

        public int Open(int at)
        {
            _opens.Add(at);
            _closeOf.Add(-1);
            _removed.Add(false);
            return _opens.Count - 1;
        }

        public void Close(int pair, int at)
        {
            _closeOf[pair] = at;
            _closes.Add(at);
            _openOf.Add(pair);
        }

        public int OpenAt(int pair) => _opens[pair];

        public int CloseOf(int pair) => _closeOf[pair];

        public bool Removed(int pair) => _removed[pair];

        public void Remove(int pair) => _removed[pair] = true;

        /// <summary>Where the bracket opening at <paramref name="at"/> closes, or -1.</summary>
        public int Match(int at) => _opens.BinarySearch(at) is var k and >= 0 ? _closeOf[k] : -1;

        /// <summary>The pair whose bracket closes at <paramref name="at"/>, or -1.</summary>
        public int PairClosingAt(int at) => _closes.BinarySearch(at) is var k and >= 0 ? _openOf[k] : -1;

        /// <summary>Whether the bracket at <paramref name="at"/> belongs to a removed pair.</summary>
        public bool IsRemoved(string text, int at)
        {
            char c = text[at];
            if (c is '(' or '[') return _opens.BinarySearch(at) is var k and >= 0 && _removed[k];
            if (c is ')' or ']') return PairClosingAt(at) is var p and >= 0 && _removed[p];
            return false;
        }
    }

    /// <summary>Whether the pair (<paramref name="open"/>, <paramref name="close"/>) can go, its neighbors read past the pairs already removed.</summary>
    private static bool Removable(string text, int open, int close, Pairs pairs)
    {
        int left = open - 1;
        while (left >= 0 && (text[left] is ' ' or '\t' || pairs.IsRemoved(text, left))) left--;
        int right = close + 1;
        while (right < text.Length && (text[right] is ' ' or '\t' || pairs.IsRemoved(text, right))) right++;
        if (left < 0 || right >= text.Length) return false;
        var inner = Inner(text, open + 1, close, pairs);
        if (inner.Unknown) return false;

        char l = text[left];
        // a literal or a name cast to a keyword type: (byte)(2)
        if (l == ')')
            return left == open - 1 && pairs.PairClosingAt(left) is var cast and >= 0 && !pairs.Removed(cast)
                && IsAny(text, pairs.OpenAt(cast) + 1, left, Keywords) && inner.Precedence == Primary && !inner.Unary;
        int need; // the precedence the inner expression must reach
        bool binaryLeft = false;
        if (IsPart(l))
        {
            int k = left;
            while (k >= 0 && IsPart(text[k])) k--;
            if (left - k != 6 || string.CompareOrdinal(text, k + 1, "return", 0, 6) != 0) return false; // a call, or a statement's parentheses
            need = 2;
        }
        else if (l is '(' or '[' or ',' or ';' or '{' or '}') need = 2;
        else if (l == '>' && left == open - 1) return false; // F<int>(x): a call
        else if (Operator(text, left, out int opStart) is { } op)
        {
            if (op == "=>" || Assignments.Contains(op)) need = 2;
            else if (op is "?" or ":") need = 3;
            else if (op is "-" or "+" or "&" or "*" or "^" && !OperandEnds(text, opStart)) return false; // unary
            else if (Binary(op) is var p && p > 0 && Clear(inner.Operator, op))
            {
                need = p + 1;
                binaryLeft = true;
            }
            else return false;
        }
        else return false;
        if (binaryLeft && inner.Unary) return false;

        char r = text[right];
        if (r is ')' or ']' or ',' or ';' or '}') { }
        else if (Operator(text, right, out _, forward: true) is { } op)
        {
            if (op is "?" or ":")
            {
                if (op == "?" && right + 1 < text.Length && text[right + 1] is '.' or '[') return false;
                need = Math.Max(need, 3);
            }
            else if (Binary(op) is var p && p > 0 && Clear(inner.Operator, op)) need = Math.Max(need, p is 10 or 9 or 3 ? p + 1 : p);
            else return false;
        }
        else return false;
        return inner.Precedence >= need;
    }

    /// <summary>
    /// What the expression between two parentheses is, read at its own depth: the precedence of its loosest operator,
    /// whether it starts with a unary operator, whether it is one literal or name, or whether the scan cannot tell.
    /// </summary>
    private readonly record struct Expression(int Precedence, string? Operator, bool Unary, bool Single, bool Unknown);

    private static readonly Expression Unknown = new(0, null, false, false, true);

    /// <summary>
    /// Whether an operand whose loosest operator is <paramref name="inner"/> (null: none) reads plainly beside
    /// <paramref name="outer"/> without its parentheses: arithmetic in arithmetic or a comparison (<c>a + b * c</c>,
    /// <c>a + b &lt; c</c>), a comparison in a logical operator (<c>a &lt; b &amp;&amp; c</c>), an operator in itself
    /// (<c>a &amp; b &amp; c</c>). Other mixes keep them, as a reader would (<c>(x &gt;&gt; k) &amp; 1</c>, <c>x &gt;&gt; (64 - n)</c>).
    /// </summary>
    private static bool Clear(string? inner, string outer)
    {
        if (inner == null) return true;
        int i = Binary(inner), o = Binary(outer);
        if (i is 12 or 13) return o is 12 or 13 or 10 or 9;
        if (i is 10 or 9) return o is 5 or 4;
        return inner == outer;
    }

    private static Expression Inner(string text, int from, int to, Pairs pairs)
    {
        int precedence = Primary, operands = 0;
        string? loosest = null;
        bool expectOperand = true, unary = false, single = true;
        for (int i = from; i < to;)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }
            int literal = SkipLiteral(text, i);
            if (literal != i)
            {
                if (c == '/' || !expectOperand || literal > to) return Unknown; // a comment, or a literal after an operand
                expectOperand = false;
                operands++;
                i = literal;
                continue;
            }
            if (c is '(' or '[')
            {
                int end = pairs.Match(i);
                if (end < 0 || end >= to || c == '[' && expectOperand) return Unknown;
                if (expectOperand)
                {
                    // (T)x: a cast, still before its operand; (a + b) an operand
                    int after = end + 1;
                    while (after < to && text[after] == ' ') after++;
                    if (after < to && (IsStart(text[after]) || char.IsDigit(text[after]) || text[after] is '(' or '"' or '\'')) single = false;
                    else
                    {
                        expectOperand = false;
                        operands++;
                    }
                }
                // f(x), a[i]: part of the operand
                i = end + 1;
                continue;
            }
            if (IsStart(c) || char.IsDigit(c))
            {
                if (!expectOperand) return Unknown; // x is T, new T(...)
                int e = char.IsDigit(c) ? NumberEnd(text, i) : IdentifierEnd(text, i);
                if (e + 2 < to && text[e] == ':' && text[e + 1] == ':' && IsStart(text[e + 2])) e = IdentifierEnd(text, e + 2); // global::
                if (!char.IsDigit(c)) e = Generic(text, e, to);
                while (e + 1 < to && text[e] == '.' && IsStart(text[e + 1])) e = Generic(text, IdentifierEnd(text, e + 1), to);
                expectOperand = false;
                operands++;
                i = e;
                continue;
            }
            if (c == '.' && !expectOperand && i + 1 < to && IsStart(text[i + 1]))
            {
                i = Generic(text, IdentifierEnd(text, i + 1), to);
                continue;
            }
            if (OperatorChars.IndexOf(c) < 0) return Unknown; // braces, a comma, ...
            int opEnd = OperatorEnd(text, i, to);
            string op = Token(text, i, opEnd);
            i = opEnd;
            if (expectOperand)
            {
                if (op is not ("-" or "+" or "!" or "~" or "++" or "--")) return Unknown;
                if (operands == 0 && op is "-" or "+" or "--" or "++") unary = true;
                single = false;
                continue;
            }
            if (op is "++" or "--" or "!") continue; // postfix
            int p = op is "?" or ":" ? 2 : Binary(op);
            if (p <= 0) return Unknown; // an assignment, a lambda, ...
            single = false;
            if (p < precedence) loosest = op; // the first of its level: a level's operators read alike beside another (Clear)
            precedence = Math.Min(precedence, p);
            expectOperand = true;
        }
        if (expectOperand || operands == 0) return Unknown;
        return new Expression(precedence, loosest, unary, single && operands == 1, false);
    }

    /// <summary>After a name at <paramref name="e"/>'s left, its type arguments when they are (<c>Create&lt;int&gt;(</c>, <c>Vector&lt;float&gt;.Count</c>).</summary>
    private static int Generic(string text, int e, int to)
    {
        if (e >= to || text[e] != '<') return e;
        int g = GenericEnd(text, e);
        return g > 0 && g <= to && (g == to || text[g] is '(' or '.' or ')' or ',' or ';' or '[' or ']') ? g : e;
    }

    /// <summary>The binary operators' precedence (higher binds tighter), or 0.</summary>
    private static int Binary(string op) => op switch
    {
        "*" or "/" or "%" => 13,
        "+" or "-" => 12,
        "<<" or ">>" or ">>>" => 11,
        "<" or ">" or "<=" or ">=" => 10,
        "==" or "!=" => 9,
        "&" => 8,
        "^" => 7,
        "|" => 6,
        "&&" => 5,
        "||" => 4,
        "??" => 3,
        _ => 0,
    };

    private static readonly HashSet<string> Assignments = new(StringComparer.Ordinal)
    {
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>=", "??=",
    };

    private const string OperatorChars = "+-*/%&|^<>=!?:~";

    /// <summary>
    /// The operator token ending at <paramref name="at"/> (or, <paramref name="forward"/>, starting there), or null when
    /// the character there is no operator's or the run of operator characters is not one token; <paramref name="start"/>:
    /// where it starts.
    /// </summary>
    private static string? Operator(string text, int at, out int start, bool forward = false)
    {
        start = at;
        if (OperatorChars.IndexOf(text[at]) < 0) return null;
        if (forward) return Token(text, at, OperatorEnd(text, at, text.Length));
        int k = at;
        while (k > 0 && OperatorChars.IndexOf(text[k - 1]) >= 0) k--;
        start = k;
        return OperatorEnd(text, k, at + 1) == at + 1 ? Token(text, k, at + 1) : null;
    }

    /// <summary>The operator token <paramref name="text"/>[<paramref name="from"/>, <paramref name="to"/>), one of the strings above (no copy).</summary>
    private static string Token(string text, int from, int to)
    {
        if (to - from == 1) return Singles[OperatorChars.IndexOf(text[from])];
        foreach (var op in Operators)
            if (op.Length == to - from && string.CompareOrdinal(text, from, op, 0, op.Length) == 0) return op;
        return text.Substring(from, to - from);
    }

    private static readonly string[] Singles = [.. OperatorChars.Select(c => c.ToString())];

    private static readonly string[] Operators =
    [
        ">>>=", ">>>", "<<=", ">>=", "??=", "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "<<", ">>", "+=", "-=", "*=", "/=",
        "%=", "&=", "|=", "^=", "++", "--", "->",
    ];

    /// <summary>The end of the operator token at <paramref name="i"/>, the longest that ends by <paramref name="to"/>.</summary>
    private static int OperatorEnd(string text, int i, int to)
    {
        foreach (var op in Operators)
            if (i + op.Length <= to && string.CompareOrdinal(text, i, op, 0, op.Length) == 0) return i + op.Length;
        return i + 1;
    }

    /// <summary>Whether the token before <paramref name="i"/> ends an operand (so the operator at <paramref name="i"/> is binary).</summary>
    private static bool OperandEnds(string text, int i)
    {
        int k = i - 1;
        while (k >= 0 && text[k] is ' ' or '\t') k--;
        return k >= 0 && (IsPart(text[k]) || text[k] is ')' or ']' or '"' or '\'');
    }

    /// <summary>The end of the number at <paramref name="i"/> (<c>1.5f</c>, <c>0x1F</c>, <c>1e-5</c>).</summary>
    private static int NumberEnd(string text, int i)
    {
        bool hex = i + 1 < text.Length && text[i] == '0' && text[i + 1] is 'x' or 'X';
        for (i++; i < text.Length; i++)
        {
            char c = text[i];
            if (IsPart(c)) continue;
            if (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])) continue;
            if (c is '+' or '-' && !hex && text[i - 1] is 'e' or 'E') continue;
            break;
        }
        return i;
    }

    // ---------------- tokens ----------------

    private static bool IsStart(char c) => (uint)((c | 0x20) - 'a') < 26 || c == '_' || c == '@' || c > 127 && char.IsLetter(c);

    private static bool IsPart(char c) => (uint)((c | 0x20) - 'a') < 26 || (uint)(c - '0') < 10 || c == '_' || c == '@' || c > 127 && char.IsLetterOrDigit(c);

    private static int IdentifierEnd(string text, int i)
    {
        i++;
        while (i < text.Length && IsPart(text[i])) i++;
        return i;
    }

    /// <summary>The word before <paramref name="i"/> (spaces skipped), or "" when the token there is not a word (<paramref name="prev"/>: its last character).</summary>
    private static bool WordBefore(string text, int i, string word, out char prev)
    {
        int k = i - 1;
        while (k >= 0 && text[k] is ' ' or '\t') k--;
        prev = k >= 0 ? text[k] : '\0';
        if (k < 0 || !IsPart(text[k])) return false;
        int end = k + 1;
        while (k >= 0 && IsPart(text[k])) k--;
        return end - k - 1 == word.Length && string.CompareOrdinal(text, k + 1, word, 0, word.Length) == 0;
    }

    /// <summary>The end of the type arguments opened at <paramref name="i"/> (after their <c>&gt;</c>), or -1 when they are not type arguments.</summary>
    private static int GenericEnd(string text, int i)
    {
        int depth = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<') depth++;
            else if (c == '>')
            {
                if (--depth == 0) return i + 1;
            }
            else if (!(IsPart(c) || c is '.' or ',' or ' ' or ':' or '[' or ']' or '?' or '(' or ')')) return -1;
        }
        return -1;
    }

    /// <summary>Where the line at <paramref name="i"/> ends when it is a preprocessor line, else <paramref name="i"/>.</summary>
    private static int Preprocessor(string text, int i)
    {
        int k = i;
        while (k < text.Length && text[k] is ' ' or '\t' or '\uFEFF') k++;
        if (k >= text.Length || text[k] != '#') return i;
        int end = text.IndexOf('\n', k);
        return end < 0 ? text.Length : end + 1;
    }

    /// <summary>Where the comment, string or character literal at <paramref name="i"/> ends, or <paramref name="i"/> when none starts there.</summary>
    private static int SkipLiteral(string text, int i)
    {
        char c = text[i];
        int n = text.Length;
        if (c == '/' && i + 1 < n && text[i + 1] == '/')
        {
            int end = text.IndexOf('\n', i);
            return end < 0 ? n : end;
        }
        if (c == '/' && i + 1 < n && text[i + 1] == '*')
        {
            int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? n : end + 2;
        }
        if (c == '"')
        {
            if (i + 2 < n && text[i + 1] == '"' && text[i + 2] == '"')
            {
                int end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                return end < 0 ? n : end + 3;
            }
            bool verbatim = i > 0 && (text[i - 1] == '@' || i > 1 && text[i - 1] == '$' && text[i - 2] == '@');
            for (int k = i + 1; k < n; k++)
            {
                if (!verbatim && text[k] == '\\') k++;
                else if (text[k] == '"')
                {
                    if (verbatim && k + 1 < n && text[k + 1] == '"') k++;
                    else return k + 1;
                }
            }
            return n;
        }
        if (c == '\'')
        {
            int k = i + 1;
            if (k < n && text[k] == '\\') k += 2;
            else k++;
            while (k < n && text[k] != '\'' && text[k] != '\n') k++;
            return k < n ? k + 1 : n;
        }
        return i;
    }
}
