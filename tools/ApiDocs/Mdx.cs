using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ApiDocs;

/// <summary>XML documentation to MDX: inline tags become Markdown, crefs become links to the reference pages.</summary>
public sealed partial class Mdx(Func<string, string?> link)
{
    private readonly List<string> _blocks = []; // code blocks of the element being rendered, kept out of the whitespace pass

    /// <summary>The Markdown of an element's content (<c>summary</c>, <c>remarks</c>, <c>param</c>...), or "" when absent.</summary>
    public string Render(XElement? element)
    {
        if (element == null) return "";
        _blocks.Clear();
        var text = new StringBuilder();
        Append(text, element);
        // Whitespace runs inside a paragraph become one space; paragraph breaks (\n\n) and list items (\n- ) stay.
        var lines = text.ToString().Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        var markdown = Breaks().Replace(string.Join("\n", lines), "\n\n").Trim();
        for (int i = 0; i < _blocks.Count; i++) markdown = markdown.Replace($"\u0001{i}\u0001", _blocks[i]);
        return markdown;
    }

    /// <summary>The first sentence of a summary as plain text (frontmatter descriptions).</summary>
    public static string Sentence(string markdown)
    {
        var plain = Links().Replace(markdown, "$1").Replace("`", "").Replace("\\{", "{").Replace("\\}", "}")
            .Replace("&lt;", "<").Replace("&gt;", ">").Replace('\n', ' ');
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        return (end < 0 ? plain : plain[..(end + 1)]).Trim();
    }

    private void Append(StringBuilder text, XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t)
            {
                text.Append(Escape(t.Value.Replace('\n', ' ')));
                continue;
            }

            if (node is not XElement e) continue;
            switch (e.Name.LocalName)
            {
                case "c": text.Append(Code(e.Value)); break;
                case "code":
                    text.Append("\n\n\u0001").Append(_blocks.Count).Append("\u0001\n\n");
                    _blocks.Add("```csharp\n" + Dedent(e.Value) + "\n```");
                    break;
                case "para": text.Append("\n\n"); Append(text, e); text.Append("\n\n"); break;
                case "br": text.Append("\n\n"); break;
                case "paramref" or "typeparamref": text.Append(Code((string?)e.Attribute("name") ?? "")); break;
                case "see" or "seealso": text.Append(See(e)); break;
                case "list":
                    text.Append("\n\n");
                    foreach (var item in e.Elements("item"))
                    {
                        text.Append("\n- ");
                        if (item.Element("term") is { } term)
                        {
                            Append(text, term);
                            text.Append(": ");
                            Append(text, item.Element("description") ?? new XElement("description"));
                        }
                        else Append(text, item);
                    }

                    text.Append("\n\n");
                    break;
                case "b" or "strong": text.Append("**"); Append(text, e); text.Append("**"); break;
                case "i" or "em": text.Append('*'); Append(text, e); text.Append('*'); break;
                default: Append(text, e); break;
            }
        }
    }

    private string See(XElement e)
    {
        if (e.Attribute("langword") is { } word) return Code(word.Value);
        if (e.Attribute("href") is { } href) return $"[{(e.Value.Length != 0 ? Escape(e.Value) : href.Value)}]({href.Value})";
        string cref = (string?)e.Attribute("cref") ?? "";
        string label = e.Value.Length != 0 ? Escape(e.Value) : Code(Label(cref));
        return link(cref) is { } target ? $"[{label}]({target})" : label;
    }

    /// <summary>A readable name for a documentation id: <c>M:EasyEcs.World.Update``1</c> is <c>World.Update</c>.</summary>
    public static string Label(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        name = Arity().Replace(name, "");
        if (name.StartsWith("EasyEcs.", StringComparison.Ordinal)) name = name["EasyEcs.".Length..];
        else if (name.Contains('.')) name = name[(name.LastIndexOf('.') + 1)..];
        return name.Replace("#ctor", "constructor");
    }

    public static string Code(string code) => code.Contains('`') ? $"`` {code} ``" : $"`{code}`";

    /// <summary>Text safe in MDX: braces start expressions and angle brackets start tags.</summary>
    public static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("<", "&lt;").Replace(">", "&gt;").Replace("*", "\\*");

    private static string Dedent(string code)
    {
        var lines = code.Replace("\r", "").Trim('\n').Split('\n');
        int indent = lines.Where(l => l.Trim().Length != 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).TrimEnd();
    }

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Breaks();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Links();

    [GeneratedRegex(@"`+\d+")]
    private static partial Regex Arity();
}
