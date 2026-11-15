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

    /// <summary>
    /// A qualified name: where it starts, where its dotted identifiers end, and where its type arguments end (-1: none);
    /// the hashes of its text to <see cref="End"/> and to <see cref="GenericEnd"/>, and its dots with the hash of the text
    /// before each (<see cref="Scratch.Dots"/>[<see cref="Dots"/>, + <see cref="DotCount"/>)): every prefix a pass looks up
    /// is hashed once, as <see cref="Parse"/> reads it.
    /// </summary>
    private readonly record struct Name(int Start, int End, int GenericEnd, int Hash, int GenericHash, int Dots, int DotCount);

    /// <summary>
    /// <paramref name="text"/> with the qualified type names (<c>global::EasyEcs.Benchmark.KernelBenchmark.Heat</c>) it
    /// names twice or more written through a file-level alias (<c>__Heat</c>), added to <paramref name="aliases"/> (alias
    /// to target, which already holds the names <see cref="Code.Aliased"/>'s BCL pass gave). A name is a type where its
    /// position says so: declared (<c>global::X x</c>), a type argument, created, the type of a member called or read
    /// (<c>global::X.M(</c>), a base, or an attribute; a type with its arguments (<c>IForEachBatch&lt;A, B&gt;</c>) gets
    /// one alias of its own when its arguments are all qualified. An alias of a namespace taken for a type would still
    /// be right: only a name of a member must never get one, and a member is never in those positions.
    /// The lists it returns are the thread's scratch (<see cref="Scratch"/>): read before the next call on the thread.
    /// </summary>
    public static List<(int Start, int End, string Alias)> Types(string text, SortedDictionary<string, string> aliases, EquatableArray<string> declared)
    {
        var scratch = Scratch.Of(text);
        var edits = scratch.Edits;
        var names = scratch.Names;
        var spans = scratch.Spans;
        var types = scratch.Types;
        var closed = scratch.Closed;
        var reserved = scratch.Reserved; // and every __ name of the text (Used)
        var existing = scratch.Existing; // target -> alias written by the template
        foreach (var pair in aliases)
        {
            reserved.Add(pair.Key);
            existing[pair.Value] = pair.Key;
        }
        for (int k = 0; k < declared.Length; k++) reserved.Add(declared[k]); // the user's own __ names (Code.Reserved)
        // the template's own alias directives (using __ForEach = ...;)
        for (int u = text.StartsWith("using __", StringComparison.Ordinal) ? 0 : text.IndexOf("\nusing __", StringComparison.Ordinal); u >= 0;
             u = text.IndexOf("\nusing __", u + 1, StringComparison.Ordinal))
        {
            int from = text[u] == '\n' ? u + 1 : u, to = text.IndexOf('\n', from);
            Existing(text, from, to < 0 ? text.Length : to, existing, reserved);
        }
        // each global:: name in code (IndexOf finds them; a line's comment, string or directive leaves one out)
        var line = new Line(-1);
        for (int i = text.IndexOf("global::", StringComparison.Ordinal); i >= 0; i = text.IndexOf("global::", i + 8, StringComparison.Ordinal))
        {
            if (i > 0 && (IsPart(text[i - 1]) || text[i - 1] == '.') || !InCode(text, i, ref line) || Parse(text, i, scratch.Dots) is not { } name) continue;
            names.Add(name);
            Classify(text, name, types, closed, scratch.Dots);
        }
        if (names.Count == 0) return edits;

        // What each name becomes: a closed generic's alias, else its longest prefix known as a type.
        var targets = scratch.Targets;
        var uses = scratch.Uses;
        int skipUntil = 0;
        foreach (var name in names)
        {
            if (name.Start < skipUntil) continue;
            Span? target = null;
            bool attribute = false;
            int end = 0;
            if (name.GenericEnd > 0 && Usage(text, name) && new Span(name.Start, name.GenericEnd - name.Start, name.GenericHash) is var whole
                && closed.TryGetValue(whole, out int count) && (count >= 2 || Had(text, whole, false, existing) != null) && Qualified(text, name.End + 1, name.GenericEnd - 1))
            {
                target = whole;
                end = name.GenericEnd;
                skipUntil = end;
            }
            else
            {
                if (name.GenericEnd < 0 && IsAttribute(text, name) && IsAny(text, name.Start, name.End, Attributes))
                {
                    target = new Span(name.Start, name.End - name.Start, name.Hash);
                    attribute = true;
                    end = name.End;
                }
                else
                    // the whole chain (a generic type is not its non-generic namesake), then before each dot, longest first
                    for (int d = name.DotCount - (name.GenericEnd > 0 ? 1 : 0); d >= 0; d--)
                    {
                        var (k, hash) = d == name.DotCount ? (name.End, name.Hash) : scratch.Dots[name.Dots + d];
                        var prefix = new Span(name.Start, k - name.Start, hash);
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

        // An alias for each target named twice or more (or that already has one), in the order of the targets' text;
        // names the file does not use. A target's text is copied only when it gets an alias.
        var alias = scratch.Alias;
        var chosen = scratch.Chosen;
        foreach (var pair in uses)
        {
            string? had = Had(text, pair.Key.Item1, pair.Key.Item2, existing);
            if (had == null && pair.Value < 2) continue;
            chosen.Add((pair.Key, spans.Text(pair.Key.Item1) + (pair.Key.Item2 ? "Attribute" : ""), had));
        }
        chosen.Sort(ByTarget);
        var parts = chosen.Count == 0 ? null : Parts(text, types, uses, existing, scratch);
        HashSet<int>? named = null;
        foreach (var (key, target, had) in chosen)
        {
            if (had != null)
            {
                alias[key] = had;
                continue;
            }
            string chosenAlias = AliasOf(target, reserved, text, named ??= Named(text, scratch.Named), parts);
            reserved.Add(chosenAlias);
            alias[key] = chosenAlias;
            aliases[chosenAlias] = target;
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

    private static readonly Comparison<((Span, bool) Key, string Target, string? Had)> ByTarget =
        static (a, b) => string.CompareOrdinal(a.Target, b.Target);

    /// <summary>The alias the template or the BCL pass gave the target <paramref name="target"/> (plus "Attribute" for an <paramref name="attribute"/>), or null.</summary>
    private static string? Had(string text, Span target, bool attribute, Dictionary<string, string> existing)
    {
        if (existing.Count == 0) return null;
        int length = target.Length + (attribute ? 9 : 0);
        foreach (var pair in existing)
            if (pair.Key.Length == length && string.CompareOrdinal(text, target.Start, pair.Key, 0, target.Length) == 0
                && (!attribute || string.CompareOrdinal(pair.Key, target.Length, "Attribute", 0, 9) == 0)) return pair.Value;
        return null;
    }

    /// <summary>A range of the file's text and its hash: a name compared by its characters, copied only when it becomes an alias's target.</summary>
    private readonly record struct Span(int Start, int Length, int Hash);

    /// <summary>Equality of <see cref="Span"/>s of the file's text (<see cref="Text"/>, set per file) by their characters.</summary>
    private sealed class Spans : IEqualityComparer<Span>
    {
        public string Source = "";

        public string Text(Span s) => Source.Substring(s.Start, s.Length);

        public bool Equals(Span a, Span b) =>
            a.Hash == b.Hash && a.Length == b.Length && string.CompareOrdinal(Source, a.Start, Source, b.Start, a.Length) == 0;

        public int GetHashCode(Span s) => s.Hash;
    }

    /// <summary>
    /// The collections both passes fill, one set per thread, cleared per file and kept with their capacity: a file's
    /// tidying allocates only what it writes. A generator thread runs one file's passes at a time.
    /// </summary>
    private sealed class Scratch
    {
        [ThreadStatic] private static Scratch? _thread;

        public readonly Spans Spans = new();
        public readonly List<Name> Names = [];

        /// <summary>The dots of the names (<see cref="Name.Dots"/>): where each is and the hash of the name's text before it.</summary>
        public readonly List<(int At, int Hash)> Dots = [];
        public readonly HashSet<Span> Types;
        public readonly Dictionary<Span, int> Closed;
        public readonly List<(int Start, int End, Span Target, bool Attribute)> Targets = [];
        public readonly Dictionary<(Span, bool), int> Uses;
        public readonly Dictionary<(Span, bool), string> Alias;
        public readonly List<((Span, bool) Key, string Target, string? Had)> Chosen = [];
        public readonly HashSet<string> Reserved = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Existing = new(StringComparer.Ordinal);
        public readonly HashSet<int> Named = [];
        public readonly Dictionary<int, int> Simple = [];
        public readonly List<(int Start, int End, string Alias)> Edits = [];
        public readonly Pairs Pairs = new();
        public readonly Stack<int> Open = new();
        public readonly List<int> Dropped = [];

        private Scratch()
        {
            Types = new HashSet<Span>(Spans);
            Closed = new Dictionary<Span, int>(Spans);
            var keys = new SpansOrAttribute(Spans);
            Uses = new Dictionary<(Span, bool), int>(keys);
            Alias = new Dictionary<(Span, bool), string>(keys);
        }

        /// <summary>The thread's scratch, emptied, for <paramref name="text"/>.</summary>
        public static Scratch Of(string text)
        {
            var s = _thread ??= new Scratch();
            s.Spans.Source = text;
            s.Names.Clear();
            s.Dots.Clear();
            s.Types.Clear();
            s.Closed.Clear();
            s.Targets.Clear();
            s.Uses.Clear();
            s.Alias.Clear();
            s.Chosen.Clear();
            s.Reserved.Clear();
            s.Existing.Clear();
            s.Named.Clear();
            s.Edits.Clear();
            return s;
        }

        /// <summary>The thread's scratch for <see cref="Parentheses"/>, its bracket lists emptied.</summary>
        public static Scratch ForPairs()
        {
            var s = _thread ??= new Scratch();
            s.Pairs.Clear();
            s.Open.Clear();
            s.Dropped.Clear();
            return s;
        }
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
    public static void Write(Chars sb, string text, int from, List<(int Start, int End, string Alias)> edits, List<int> dropped)
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
    /// The names come in order, so <paramref name="line"/> carries the scan of their line from one to the next: each
    /// line's text is read once, however many names it holds.
    /// </summary>
    private static bool InCode(string text, int at, ref Line line)
    {
        if (line.End < 0 || at >= line.End)
        {
            int start = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1, end = text.IndexOf('\n', at);
            int k = start;
            while (k < at && text[k] is ' ' or '\t' or '\uFEFF') k++;
            bool directive = k < at && text[k] == '#' || string.CompareOrdinal(text, k, "using ", 0, 6) == 0;
            line = new Line(end < 0 ? text.Length + 1 : end) { At = k, Dead = directive };
        }
        if (line.Dead || at < line.At) return false; // a directive or a comment; inside a literal read before
        for (int k = line.At; k < at; k++)
        {
            char c = text[k];
            if (c == '/' && k + 1 < at && text[k + 1] == '/')
            {
                line.Dead = true;
                return false;
            }
            if (c is '"' or '\'')
            {
                int end = SkipLiteral(text, k);
                if (end > at)
                {
                    line.At = end;
                    return false;
                }
                k = end - 1;
            }
        }
        line.At = at;
        return true;
    }

    /// <summary>
    /// <see cref="InCode"/>'s line: where it ends (-1: none read yet), how far it is read (<see cref="At"/>: the next
    /// character to read, or a literal's end past the last name), and whether the rest is no code (<see cref="Dead"/>).
    /// </summary>
    private struct Line(int end)
    {
        public readonly int End = end;
        public int At;
        public bool Dead;
    }

    /// <summary>
    /// The name of a qualified name at <paramref name="i"/> (its <c>global::</c>), or null when no identifier follows; its
    /// dots go into <paramref name="dots"/>.
    /// </summary>
    private static Name? Parse(string text, int i, List<(int At, int Hash)> dots)
    {
        int j = i + 8, n = text.Length;
        if (j >= n || !IsStart(text[j])) return null;
        int first = dots.Count;
        unchecked
        {
            int hash = HashFrom(Seed, text, i, j);
            while (true)
            {
                hash = (hash ^ text[j++]) * Prime;
                while (j < n && IsPart(text[j])) hash = (hash ^ text[j++]) * Prime;
                if (j + 1 >= n || text[j] != '.' || !IsStart(text[j + 1])) break;
                dots.Add((j, hash));
                hash = (hash ^ '.') * Prime;
                j++;
            }
            int generic = j < n && text[j] == '<' ? GenericEnd(text, j) : -1;
            return new Name(i, j, generic, hash, generic > 0 ? HashFrom(hash, text, j, generic) : 0, first, dots.Count - first);
        }
    }

    /// <summary>Records what <paramref name="name"/>'s position proves: the name or its left side a type, or a closed generic type.</summary>
    private static void Classify(string text, Name name, HashSet<Span> types, Dictionary<Span, int> closed, List<(int At, int Hash)> dots)
    {
        int after = name.GenericEnd > 0 ? name.GenericEnd : name.End;
        char next = after < text.Length ? text[after] : '\0';
        bool afterNew = WordBefore(text, name.Start, "new", out char prev);
        if (name.GenericEnd > 0)
        {
            if (next == '(' && !afterNew) Left(name, types, dots); // a generic method's type
            else
            {
                var whole = new Span(name.Start, name.GenericEnd - name.Start, name.GenericHash);
                closed[whole] = closed.TryGetValue(whole, out int c) ? c + 1 : 1;
            }
            return;
        }
        var chain = new Span(name.Start, name.End - name.Start, name.Hash);
        if (next == '(')
        {
            if (afterNew) types.Add(chain);
            else if (!IsAttribute(text, name)) Left(name, types, dots); // a member called: its left side is a type
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
    private static void Left(Name name, HashSet<Span> types, List<(int At, int Hash)> dots)
    {
        if (name.DotCount == 0) return;
        var (dot, hash) = dots[name.Dots + name.DotCount - 1];
        types.Add(new Span(name.Start, dot - name.Start, hash));
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
    private static string AliasOf(string target, HashSet<string> reserved, string text, HashSet<int> named, Dictionary<string, string>? parts)
    {
        bool Taken(string name) => reserved.Contains(name) || named.Contains(Hash(name, 0, name.Length)) && Used(text, name);
        int open = target.IndexOf('<');
        string chain = open < 0 ? target : target.Substring(0, open);
        string[] segments = chain.Substring(8).Split('.');
        string simple = SimpleName(segments[segments.Length - 1]);
        // a type whose simple name another of the file shares by its outer names too (__A_Position, __B_Position); a
        // closed generic by its arguments too (__IForEachBatch_P3_V3, __Span_A_Position), so two of one file read apart
        string own = parts != null && parts.TryGetValue(chain, out var part) ? part : simple;
        string first = open < 0 ? "__" + own
            : "__" + own + "_" + ArgumentNames(target, open, parts);
        if (!Taken(first)) return first;
        string second = open < 0 && segments.Length > 1 ? "__" + segments[segments.Length - 2].TrimStart('@') + "_" + simple : first;
        if (!Taken(second)) return second;
        for (int k = 2; ; k++)
            if (!Taken(second + k)) return second + k;
    }

    /// <summary>
    /// The last identifier of each type argument of <paramref name="target"/> (from its <c>&lt;</c> at
    /// <paramref name="open"/>), each a run of name characters followed by <c>,</c> <c>&gt;</c> <c>[</c> or <c>?</c>
    /// (spaces between), without its <c>@</c>, joined by <c>_</c>: <c>P3_V3</c>.
    /// </summary>
    private static string ArgumentNames(string target, int open, Dictionary<string, string>? parts)
    {
        var names = new StringBuilder();
        for (int i = open; i < target.Length;)
        {
            if (!IsWord(target[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < target.Length && IsWord(target[i])) i++;
            int next = i;
            while (next < target.Length && char.IsWhiteSpace(target[next])) next++;
            if (next >= target.Length || target[next] is not (',' or '>' or '[' or '?')) continue;
            if (names.Length > 0) names.Append('_');
            // the argument's whole name (global::Game.A.Position), whose part tells it from the file's other Positions
            int whole = start;
            while (whole > open + 1 && target[whole - 1] == '.') for (whole--; whole > open + 1 && IsWord(target[whole - 1]);) whole--;
            if (parts != null && whole >= 8 && string.CompareOrdinal(target, whole - 8, "global::", 0, 8) == 0
                && parts.TryGetValue(target.Substring(whole - 8, i - whole + 8), out var part))
            {
                names.Append(part);
                continue;
            }
            while (start < i && target[start] == '@') start++;
            names.Append(target, start, i - start);
        }
        return names.ToString();

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '@';
    }

    /// <summary>A type's last name as its alias says it: without its <c>@</c>, an attribute without <c>Attribute</c>.</summary>
    private static string SimpleName(string last)
    {
        string simple = last.TrimStart('@');
        return simple.EndsWith("Attribute", StringComparison.Ordinal) && simple.Length > 9 ? simple.Substring(0, simple.Length - 9) : simple;
    }

    /// <summary>
    /// For each type of the file (<paramref name="types"/>, the targets of <paramref name="uses"/>, those its alias
    /// block already names: <paramref name="existing"/>) whose simple name
    /// another type of the file shares, the outer names that tell it apart as an alias says them: the fewest from its
    /// end that no other of those types ends with (<c>A_Position</c>, <c>B_Position</c>; <c>Ship_Pos</c>), keyed by its
    /// qualified name; null when no two share one, which hashes alone find without copying a name.
    /// </summary>
    private static Dictionary<string, string>? Parts(string text, HashSet<Span> types, Dictionary<(Span, bool), int> uses,
        Dictionary<string, string> existing, Scratch scratch)
    {
        var simple = scratch.Simple; // the simple name's hash -> the first qualified name's hash, or 0 once two differ
        simple.Clear();
        bool shared = false;
        foreach (var t in types) shared |= Note(text, t.Start, t.Length, simple);
        foreach (var key in uses.Keys) shared |= Note(text, key.Item1.Start, key.Item1.Length, simple);
        foreach (var target in existing.Keys) shared |= Note(target, 0, target.Length, simple);
        if (!shared) return null;
        // the names of the shared simple names, copied
        var groups = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in types) Add(text, t.Start, t.Length);
        foreach (var key in uses.Keys) Add(text, key.Item1.Start, key.Item1.Length);
        foreach (var target in existing.Keys) Add(target, 0, target.Length);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            foreach (var segments in group)
            {
                int depth = 2;
                while (depth < segments.Length && group.Exists(o => o != segments && Suffix(o, depth) == Suffix(segments, depth))) depth++;
                parts["global::" + string.Join(".", segments)] = Suffix(segments, depth);
            }
        }
        return parts;

        void Add(string source, int start, int length)
        {
            if (length < 9 || string.CompareOrdinal(source, start, "global::", 0, 8) != 0) return;
            var (simpleHash, _, end) = Hashes(source, start, length);
            if (simple.TryGetValue(simpleHash, out int first) && first != 0) return;
            string chain = source.Substring(start, end - start);
            if (!seen.Add(chain)) return;
            string[] segments = chain.Substring(8).Split('.');
            string name = SimpleName(segments[segments.Length - 1]);
            if (!groups.TryGetValue(name, out var group)) groups[name] = group = [];
            group.Add(segments);
        }

        // the last depth names joined as an alias says them (A_Position): every name when there are fewer
        static string Suffix(string[] segments, int depth)
        {
            int from = Math.Max(0, segments.Length - depth);
            var sb = new StringBuilder();
            for (int k = from; k < segments.Length; k++)
                sb.Append(k > from ? "_" : "").Append(k == segments.Length - 1 ? SimpleName(segments[k]) : segments[k].TrimStart('@', '_')); // an outer generated name without its __ (__S_Jobs: S_Jobs)
            return sb.ToString();
        }
    }

    /// <summary>Records <paramref name="span"/>'s names in <paramref name="simple"/>: true when its simple name is another qualified name's too.</summary>
    private static bool Note(string text, int start, int length, Dictionary<int, int> simple)
    {
        if (length < 9 || string.CompareOrdinal(text, start, "global::", 0, 8) != 0) return false;
        var (simpleHash, chainHash, _) = Hashes(text, start, length);
        if (chainHash == 0) chainHash = 1;
        if (!simple.TryGetValue(simpleHash, out int first))
        {
            simple[simpleHash] = chainHash;
            return false;
        }
        if (first == 0 || first == chainHash) return first == 0;
        simple[simpleHash] = 0;
        return true;
    }

    /// <summary>The hashes of a type name's simple name (as <see cref="SimpleName"/> says it) and of its qualified name, without type arguments; where those end.</summary>
    private static (int Simple, int Chain, int End) Hashes(string text, int start, int length)
    {
        int end = text.IndexOf('<', start, length);
        if (end < 0) end = start + length;
        int last = text.LastIndexOf('.', end - 1, end - start - 8) + 1;
        if (last <= start + 8) last = start + 8;
        while (last < end && text[last] == '@') last++;
        int simpleEnd = end - last > 9 && string.CompareOrdinal(text, end - 9, "Attribute", 0, 9) == 0 ? end - 9 : end;
        return (Hash(text, last, simpleEnd - last), Hash(text, start, end - start), end);
    }

    /// <summary>The hashes of the <c>__</c> names <paramref name="text"/> holds (into <paramref name="named"/>): an alias candidate whose hash is not among them is free.</summary>
    private static HashSet<int> Named(string text, HashSet<int> named)
    {
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
    private static int Hash(string text, int from, int length) => HashFrom(Seed, text, from, from + length);

    /// <summary>FNV-1a's <paramref name="hash"/> continued over <paramref name="text"/>[<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static int HashFrom(int hash, string text, int from, int to)
    {
        unchecked
        {
            for (int i = from; i < to; i++) hash = (hash ^ text[i]) * Prime;
            return hash;
        }
    }

    private const int Seed = unchecked((int)2166136261), Prime = 16777619;

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
        var scratch = Scratch.ForPairs();
        var pairs = scratch.Pairs;
        var stack = scratch.Open;
        int n = text.Length;
        int afterDirective = -1; // the start of the line after the directive skipped last, which is not read as one
        for (int i = 0; i < n;)
        {
            i = text.IndexOfAny(Brackets, i);
            if (i < 0) break;
            char c = text[i];
            if (c == '#')
            {
                // a directive: the first character of its line, the line after a directive aside
                int k = i - 1;
                while (k >= 0 && text[k] is ' ' or '\t' or '\uFEFF') k--;
                if ((k < 0 || text[k] == '\n') && k + 1 != afterDirective)
                {
                    int end = text.IndexOf('\n', i);
                    i = afterDirective = end < 0 ? n : end + 1;
                }
                else i++;
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
        var dropped = scratch.Dropped;
        for (int k = 0; k < pairs.Count; k++)
            if (pairs.Removed(k))
            {
                dropped.Add(pairs.OpenAt(k));
                dropped.Add(pairs.CloseOf(k));
            }
        dropped.Sort();
        return dropped;
    }

    /// <summary>What the bracket scan stops at: brackets, the starts of literals and comments, a directive's <c>#</c>.</summary>
    private static readonly char[] Brackets = ['(', ')', '[', ']', '"', '\'', '/', '#'];

    /// <summary>
    /// The text's bracket pairs, '(' and '[', in the order they open: where each closes, which are removed. Lookups by
    /// position are binary searches over the opens and the closes, both ascending as the scan meets them.
    /// </summary>
    private sealed class Pairs
    {
        private readonly List<int> _opens = [], _closeOf = [], _closes = [], _openOf = [];
        private readonly List<bool> _removed = [];

        public int Count => _opens.Count;

        public void Clear()
        {
            _opens.Clear();
            _closeOf.Clear();
            _closes.Clear();
            _openOf.Clear();
            _removed.Clear();
        }

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

/// <summary>
/// A text being written, in the thread's one buffer, kept with its capacity from file to file: a file's text is
/// allocated once, as the string <see cref="ToString"/> returns (a StringBuilder would allocate its buffer as well).
/// One text at a time per thread: <see cref="Empty"/> starts the next.
/// </summary>
internal sealed class Chars
{
    [ThreadStatic] private static Chars? _thread;

    private char[] _buffer = new char[1024];
    private int _length;

    /// <summary>The thread's buffer, emptied, with room for <paramref name="capacity"/> characters.</summary>
    public static Chars Empty(int capacity)
    {
        var chars = _thread ??= new Chars();
        chars._length = 0;
        if (chars._buffer.Length < capacity) chars._buffer = new char[Math.Max(capacity, chars._buffer.Length * 2)];
        return chars;
    }

    public Chars Append(string text)
    {
        Ensure(text.Length);
        text.CopyTo(0, _buffer, _length, text.Length);
        _length += text.Length;
        return this;
    }

    public Chars Append(string text, int start, int count)
    {
        Ensure(count);
        text.CopyTo(start, _buffer, _length, count);
        _length += count;
        return this;
    }

    private void Ensure(int more)
    {
        if (_length + more <= _buffer.Length) return;
        var grown = new char[Math.Max(_length + more, _buffer.Length * 2)];
        Array.Copy(_buffer, grown, _length);
        _buffer = grown;
    }

    public override string ToString() => new(_buffer, 0, _length);
}
