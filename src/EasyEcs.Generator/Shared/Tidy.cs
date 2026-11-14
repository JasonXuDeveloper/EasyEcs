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
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "global::System.ComponentModel.EditorBrowsable", "global::System.Runtime.CompilerServices.UnsafeAccessor",
        "global::System.Runtime.CompilerServices.UnsafeAccessorType", "global::System.Runtime.CompilerServices.InterceptsLocation",
        "global::System.CodeDom.Compiler.GeneratedCode", "global::System.Runtime.CompilerServices.ModuleInitializer",
        "global::System.AttributeUsage", "global::System.Runtime.CompilerServices.SkipLocalsInit",
    };

    /// <summary>Types generated code names only as the left side of a member (an enum's values in attribute arguments).</summary>
    private static readonly string[] Seeds =
    [
        "global::System.ComponentModel.EditorBrowsableState", "global::System.Runtime.CompilerServices.UnsafeAccessorKind",
        "global::System.AttributeTargets",
    ];

    /// <summary>Words that follow an expression, not a type's name (<c>global::E.A or global::E.B</c>).</summary>
    private static readonly HashSet<string> AfterExpression = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "is", "as", "when", "switch", "with", "in", "into", "on", "equals", "by", "ascending", "descending",
    };

    /// <summary>A type declaration's head or a constraint, whose names after the colon are types.</summary>
    private static readonly Regex Declaration = new(
        @"^\s*(?:(?:public|internal|private|protected|static|readonly|ref|partial|sealed|abstract|unsafe|file|new)\s+)*(?:class|struct|interface|record)\b|^\s*where\s",
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
    public static string Types(string text, SortedDictionary<string, string> aliases)
    {
        var names = new List<Name>();
        var types = new HashSet<string>(Seeds, StringComparer.Ordinal);
        var closed = new Dictionary<string, int>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(aliases.Keys, StringComparer.Ordinal);
        var existing = new Dictionary<string, string>(StringComparer.Ordinal); // target -> alias written by the template
        foreach (var pair in aliases) existing[pair.Value] = pair.Key;
        int n = text.Length;
        for (int i = 0; i < n;)
        {
            if (LineStart(text, i) is var skipped && skipped != i)
            {
                Existing(text, i, skipped, existing, reserved);
                i = skipped;
                continue;
            }
            int s = SkipLiteral(text, i);
            if (s != i)
            {
                i = s;
                continue;
            }
            char c = text[i];
            if (!IsStart(c))
            {
                i++;
                continue;
            }
            int e = IdentifierEnd(text, i);
            if (c == '_' && e - i > 2 && text[i + 1] == '_') reserved.Add(text.Substring(i, e - i));
            if (e - i == 6 && string.CompareOrdinal(text, i, "global", 0, 6) == 0 && e + 1 < n && text[e] == ':' && text[e + 1] == ':'
                && (i == 0 || !IsPart(text[i - 1]) && text[i - 1] != '.') && Parse(text, i) is { } name)
            {
                names.Add(name);
                Classify(text, name, types, closed);
                i = e + 2;
                continue;
            }
            i = e;
        }
        if (names.Count == 0) return text;

        // What each name becomes: a closed generic's alias, else its longest prefix known as a type.
        var edits = new List<(int Start, int End, string Target)>();
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        int skipUntil = 0;
        foreach (var name in names)
        {
            if (name.Start < skipUntil) continue;
            string? target = null;
            int end = 0;
            if (name.GenericEnd > 0 && Usage(text, name) && text.Substring(name.Start, name.GenericEnd - name.Start) is var whole
                && closed.TryGetValue(whole, out int count) && (count >= 2 || existing.ContainsKey(whole)) && Qualified(text, name.End + 1, name.GenericEnd - 1))
            {
                target = whole;
                end = name.GenericEnd;
                skipUntil = end;
            }
            else
            {
                if (name.GenericEnd < 0 && IsAttribute(text, name) && Attributes.Contains(Chain(text, name)))
                {
                    target = Chain(text, name) + "Attribute";
                    end = name.End;
                }
                else
                    for (int k = name.End; k > name.Start + 8; k = text.LastIndexOf('.', k - 1, k - 1 - (name.Start + 8)))
                    {
                        if (k == name.End && name.GenericEnd > 0) continue; // a generic type is not its non-generic namesake
                        if (k < 0) break;
                        string prefix = text.Substring(name.Start, k - name.Start);
                        if (!types.Contains(prefix)) continue;
                        target = prefix;
                        end = k;
                        break;
                    }
            }
            if (target == null) continue;
            edits.Add((name.Start, end, target));
            uses[target] = uses.TryGetValue(target, out int u) ? u + 1 : 1;
        }

        // An alias for each target named twice or more (or that already has one); names the file does not use
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uses.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(pair.Key, out var had))
            {
                alias[pair.Key] = had;
                continue;
            }
            if (pair.Value < 2) continue;
            string chosen = AliasOf(pair.Key, reserved);
            reserved.Add(chosen);
            alias[pair.Key] = chosen;
            aliases[chosen] = pair.Key;
        }
        if (alias.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        int at = 0;
        foreach (var (start, end, target) in edits)
        {
            if (!alias.TryGetValue(target, out var a) || start < at) continue;
            sb.Append(text, at, start - at).Append(a);
            at = end;
        }
        return sb.Append(text, at, n - at).ToString();
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
    private static void Classify(string text, Name name, HashSet<string> types, Dictionary<string, int> closed)
    {
        int after = name.GenericEnd > 0 ? name.GenericEnd : name.End;
        char next = after < text.Length ? text[after] : '\0';
        string prevWord = WordBefore(text, name.Start, out char prev);
        if (name.GenericEnd > 0)
        {
            if (next == '(' && prevWord != "new") Left(text, name, types); // a generic method's type
            else
            {
                string whole = text.Substring(name.Start, name.GenericEnd - name.Start);
                closed[whole] = closed.TryGetValue(whole, out int c) ? c + 1 : 1;
            }
            return;
        }
        string chain = Chain(text, name);
        if (next == '(')
        {
            if (prevWord == "new") types.Add(chain);
            else if (!IsAttribute(text, name)) Left(text, name, types); // a member called: its left side is a type
            return;
        }
        if (IsAttribute(text, name)) return;
        bool type = next == '>' || name.Start > 0 && text[name.Start - 1] == '<'
            || next == '[' && after + 1 < text.Length && text[after + 1] == ']'
            || prev == '(' && prevWord is "typeof" or "default" or "sizeof";
        if (!type && next == ' ' && after + 1 < text.Length && IsStart(text[after + 1]))
            type = !AfterExpression.Contains(text.Substring(after + 1, IdentifierEnd(text, after + 1) - after - 1));
        if (!type && next is ',' or '\n' or '\r' or ' ' or '{')
        {
            int line = text.LastIndexOf('\n', name.Start) + 1;
            string head = text.Substring(line, name.Start - line);
            type = head.Contains(" : ") && Declaration.IsMatch(head);
        }
        if (type) types.Add(chain);
    }

    /// <summary>The left side of <paramref name="name"/>'s last dot, a type (the name is a member of it).</summary>
    private static void Left(string text, Name name, HashSet<string> types)
    {
        int dot = text.LastIndexOf('.', name.End - 1, name.End - name.Start - 8);
        if (dot > name.Start + 8) types.Add(text.Substring(name.Start, dot - name.Start));
    }

    /// <summary>Whether the closed generic <paramref name="name"/> is used as a type, not called as a generic method.</summary>
    private static bool Usage(string text, Name name) =>
        !(name.GenericEnd < text.Length && text[name.GenericEnd] == '(' && WordBefore(text, name.Start, out _) != "new");

    /// <summary>Whether <paramref name="name"/> names an attribute (<c>[global::X(</c>, <c>[global::X]</c>).</summary>
    private static bool IsAttribute(string text, Name name) => name.Start > 0 && text[name.Start - 1] == '[';

    private static string Chain(string text, Name name) => text.Substring(name.Start, name.End - name.Start);

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
            string word = text.Substring(i, e - i);
            if (word == "global" && e + 1 < to && text[e] == ':')
            {
                i = e + 2;
                // the dotted identifiers after it
                while (i < to && (IsPart(text[i]) || text[i] == '.')) i++;
                continue;
            }
            if (!Keywords.Contains(word)) return false;
            i = e;
        }
        return true;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "float", "double", "decimal", "char",
        "string", "object",
    };

    /// <summary>An alias for <paramref name="target"/> the file does not use: <c>__Heat</c>, else with its outer name or arguments, else numbered.</summary>
    private static string AliasOf(string target, HashSet<string> reserved)
    {
        int open = target.IndexOf('<');
        string chain = open < 0 ? target : target.Substring(0, open);
        string[] segments = chain.Substring(8).Split('.');
        string simple = segments[segments.Length - 1].TrimStart('@');
        if (simple.EndsWith("Attribute", StringComparison.Ordinal) && simple.Length > 9) simple = simple.Substring(0, simple.Length - 9);
        // a closed generic by its arguments too (__IForEachBatch_P3_V3), so two of one file read apart
        string first = open < 0 ? "__" + simple
            : "__" + simple + "_" + string.Join("_", Regex.Matches(target.Substring(open), @"[\w@]+(?=\s*[,>\[?])").Cast<Match>().Select(m => m.Value.TrimStart('@')));
        if (!reserved.Contains(first)) return first;
        string second = open < 0 && segments.Length > 1 ? "__" + segments[segments.Length - 2].TrimStart('@') + "_" + simple : first;
        if (!reserved.Contains(second)) return second;
        for (int k = 2; ; k++)
            if (!reserved.Contains(second + k)) return second + k;
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
    public static string Parentheses(string text)
    {
        var match = new Dictionary<int, int>();
        var closes = new Dictionary<int, int>(); // a ')' -> its '('
        var opens = new List<int>();
        var stack = new Stack<int>();
        int n = text.Length;
        for (int i = 0; i < n;)
        {
            int s = i == 0 || text[i - 1] == '\n' ? Preprocessor(text, i) : i;
            if (s == i) s = SkipLiteral(text, i);
            if (s != i)
            {
                i = s;
                continue;
            }
            char c = text[i];
            if (c is '(' or '[')
            {
                stack.Push(i);
                if (c == '(') opens.Add(i);
            }
            else if (c is ')' or ']' && stack.Count > 0)
            {
                int open = stack.Pop();
                match[open] = i;
                closes[i] = open;
            }
            i++;
        }
        var removed = new HashSet<int>();
        foreach (int open in opens)
        {
            if (!match.TryGetValue(open, out int close) || text[close] != ')') continue;
            if (Removable(text, open, close, match, closes, removed))
            {
                removed.Add(open);
                removed.Add(close);
            }
        }
        if (removed.Count == 0) return text;
        var sorted = removed.ToArray();
        Array.Sort(sorted);
        var sb = new StringBuilder(n);
        int at = 0;
        foreach (int k in sorted)
        {
            sb.Append(text, at, k - at);
            at = k + 1;
        }
        return sb.Append(text, at, n - at).ToString();
    }

    /// <summary>Whether the pair (<paramref name="open"/>, <paramref name="close"/>) can go, its neighbors read past the pairs already removed.</summary>
    private static bool Removable(string text, int open, int close, Dictionary<int, int> match, Dictionary<int, int> closes, HashSet<int> removed)
    {
        int left = open - 1;
        while (left >= 0 && (text[left] is ' ' or '\t' || removed.Contains(left))) left--;
        int right = close + 1;
        while (right < text.Length && (text[right] is ' ' or '\t' || removed.Contains(right))) right++;
        if (left < 0 || right >= text.Length) return false;
        var inner = Inner(text, open + 1, close, match);
        if (inner.Unknown) return false;

        char l = text[left];
        // a literal or a name cast to a keyword type: (byte)(2)
        if (l == ')')
            return left == open - 1 && closes.TryGetValue(left, out int castOpen) && !removed.Contains(castOpen)
                && Keywords.Contains(text.Substring(castOpen + 1, left - castOpen - 1)) && inner.Precedence == Primary && !inner.Unary;
        int need; // the precedence the inner expression must reach
        bool binaryLeft = false;
        if (IsPart(l))
        {
            int k = left;
            while (k >= 0 && IsPart(text[k])) k--;
            if (text.Substring(k + 1, left - k) != "return") return false; // a call, or a statement's parentheses
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

    private static Expression Inner(string text, int from, int to, Dictionary<int, int> match)
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
                if (!match.TryGetValue(i, out int end) || end >= to || c == '[' && expectOperand) return Unknown;
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
            string op = text.Substring(i, opEnd - i);
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
        if (forward) return text.Substring(at, OperatorEnd(text, at, text.Length) - at);
        int k = at;
        while (k > 0 && OperatorChars.IndexOf(text[k - 1]) >= 0) k--;
        start = k;
        return OperatorEnd(text, k, at + 1) == at + 1 ? text.Substring(k, at - k + 1) : null;
    }

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

    private static bool IsStart(char c) => char.IsLetter(c) || c == '_' || c == '@';

    private static bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private static int IdentifierEnd(string text, int i)
    {
        i++;
        while (i < text.Length && IsPart(text[i])) i++;
        return i;
    }

    /// <summary>The word before <paramref name="i"/> (spaces skipped), or "" when the token there is not a word (<paramref name="prev"/>: its last character).</summary>
    private static string WordBefore(string text, int i, out char prev)
    {
        int k = i - 1;
        while (k >= 0 && text[k] is ' ' or '\t') k--;
        prev = k >= 0 ? text[k] : '\0';
        if (k < 0 || !IsPart(text[k])) return "";
        int end = k + 1;
        while (k >= 0 && IsPart(text[k])) k--;
        return text.Substring(k + 1, end - k - 1);
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

    /// <summary>
    /// Where the line at <paramref name="i"/> ends when it is a using directive or a preprocessor line (and
    /// <paramref name="i"/> starts a line), else <paramref name="i"/>.
    /// </summary>
    private static int Preprocessor(string text, int i)
    {
        int k = i;
        while (k < text.Length && text[k] is ' ' or '\t' or '﻿') k++;
        if (k >= text.Length || text[k] != '#') return i;
        int end = text.IndexOf('\n', k);
        return end < 0 ? text.Length : end + 1;
    }

    private static int LineStart(string text, int i)
    {
        if (i > 0 && text[i - 1] != '\n') return i;
        int k = i;
        while (k < text.Length && text[k] is ' ' or '\t' or '﻿') k++;
        if (k < text.Length && (text[k] == '#' || string.CompareOrdinal(text, k, "using ", 0, 6) == 0))
        {
            int end = text.IndexOf('\n', k);
            return end < 0 ? text.Length : end + 1;
        }
        return i;
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
