using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ApiDocs;

/// <summary>
/// The Chinese text of the XML documentation (<c>tools/ApiDocs/zh/&lt;page&gt;.xml</c>): one <c>member</c> per
/// documentation id, with the same elements as the English one and a hash of the English text it translates
/// (<c>source</c>). Lookups record what the pages show, so <see cref="Problems"/> can report what is missing, stale,
/// untranslated or no longer documented.
/// </summary>
public sealed partial class Translations
{
    private readonly string _directory;
    private readonly Dictionary<string, (XElement Member, string File)> _members = [];
    private readonly List<string> _problems = [];
    // documentation id to (hash of the English text, the English member, the page it is shown on), in page order
    private readonly Dictionary<string, (string Hash, XElement English, string Page)> _shown = [];
    // pages that would show an English text: they are not written (Program keeps the committed page)
    private readonly HashSet<string> _english = new(StringComparer.Ordinal);

    public Translations(string directory)
    {
        _directory = directory;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        foreach (var member in XDocument.Load(file, LoadOptions.PreserveWhitespace).Root!.Elements("member"))
        {
            string id = (string?)member.Attribute("name") ?? "";
            if (!_members.TryAdd(id, (member, Path.GetFileName(file))))
                _problems.Add($"{Path.GetFileName(file)}: {id} is translated twice (also in {_members[id].File})");
        }
    }

    /// <summary>The page being rendered: where <see cref="Scaffold"/> puts the members it shows.</summary>
    public string Page { get; set; } = "";

    /// <summary>
    /// The Chinese documentation of a member whose English documentation is <paramref name="english"/>. Without a
    /// usable translation it returns the English one and marks <see cref="Page"/> as showing English
    /// (<see cref="ShowsEnglish"/>): such a page is never written, and <see cref="Problems"/> reports why.
    /// </summary>
    public XElement Lookup(string id, XElement english)
    {
        string hash = Hash(english);
        _shown.TryAdd(id, (hash, english, Page));
        XElement? member = null;
        if (_members.TryGetValue(id, out var entry)) member = entry.Member;
        else if (Same(id, hash) is { } same) member = _members[same].Member;
        if (member != null && Usable(member, english, hash)) return member;
        _english.Add(Page);
        return english;
    }

    /// <summary>Whether <paramref name="page"/> (a slug, or <c>index</c>) holds a member without a usable translation.</summary>
    public bool ShowsEnglish(string page) => _english.Contains(page);

    // Another translated member with the same English text (a positional record's constructor and its type): the
    // same English is the same Chinese, so it is translated once.
    private string? Same(string id, string hash)
    {
        foreach (var (other, (member, _)) in _members)
            if (other != id && (string?)member.Attribute("source") == hash)
                return other;
        return null;
    }

    // A translation matches the English text it was made from and has the same elements.
    private static bool Usable(XElement zh, XElement english, string hash) =>
        (string?)zh.Attribute("source") == hash && Shape(zh) == Shape(english) && !Untranslated(zh);

    /// <summary>Everything that keeps the Chinese reference from matching the English one, one line each.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>(_problems);
        foreach (var (id, (hash, english, page)) in _shown)
        {
            if (!_members.TryGetValue(id, out var entry))
            {
                if (Same(id, hash) != null) continue;
                problems.Add($"{page}.xml: {id} has English docs but no translation (`dotnet run --project tools/ApiDocs -- --zh-scaffold` adds it)");
                continue;
            }

            string file = entry.File;
            if ((string?)entry.Member.Attribute("source") != hash)
                problems.Add($"{file}: {id} translates an older English text: update it to the English below, then set source=\"{hash}\"\n    {Flat(english)}");
            if (Shape(entry.Member) != Shape(english))
                problems.Add($"{file}: {id} has elements {Shape(entry.Member)}, the English has {Shape(english)}");
            if (Untranslated(entry.Member))
                problems.Add($"{file}: {id} is not translated yet (no Chinese text)");
        }

        foreach (var (id, (_, file)) in _members)
            if (!_shown.ContainsKey(id))
                problems.Add($"{file}: {id} is not documented any more (or not shown): delete the entry");
        return problems;
    }

    /// <summary>
    /// Adds every shown member without a translation to its page's file, with the English text to translate
    /// (reported as untranslated until it holds Chinese).
    /// </summary>
    public int Scaffold()
    {
        int added = 0;
        var hashes = new HashSet<string>(_members.Values.Select(m => (string?)m.Member.Attribute("source") ?? ""));
        foreach (var group in _shown.Where(s => !_members.ContainsKey(s.Key) && hashes.Add(s.Value.Hash)).GroupBy(s => s.Value.Page))
        {
            string path = Path.Combine(_directory, group.Key + ".xml");
            var doc = File.Exists(path) ? XDocument.Load(path, LoadOptions.PreserveWhitespace) : new XDocument(new XElement("doc"));
            foreach (var (id, (hash, english, _)) in group)
            {
                var member = new XElement("member", new XAttribute("name", id), new XAttribute("source", hash), english.Nodes());
                doc.Root!.Add("\n  ", member);
                added++;
            }

            doc.Root!.Add("\n");
            Directory.CreateDirectory(_directory);
            File.WriteAllText(path, doc.Declaration == null ? doc.ToString(SaveOptions.DisableFormatting) + "\n" : doc.ToString());
        }

        return added;
    }

    /// <summary>The hash of an English member's text, whitespace-insensitive: what a translation was made from.</summary>
    public static string Hash(XElement english) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Flat(english))))[..12];

    private static string Flat(XElement element) =>
        Whitespace().Replace(string.Concat(element.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))), " ").Trim();

    // The elements of a member and the names they document: summary, param:x, exception:T:...
    private static string Shape(XElement member) =>
        string.Join(" ", member.Elements().Select(e => e.Name.LocalName + ((string?)e.Attribute("name") ?? (string?)e.Attribute("cref")) switch
        {
            null => "",
            var key => ":" + key,
        }));

    // Prose (code and references left out) with letters but no Chinese.
    private static bool Untranslated(XElement member)
    {
        var text = new StringBuilder();
        void Prose(XElement e)
        {
            foreach (var node in e.Nodes())
                if (node is XText t) text.Append(t.Value);
                else if (node is XElement { Name.LocalName: not ("c" or "code" or "see" or "seealso" or "paramref" or "typeparamref") } child) Prose(child);
        }

        Prose(member);
        string prose = text.ToString();
        return Letters().IsMatch(prose) && !Cjk().IsMatch(prose);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[A-Za-z]{3,}")]
    private static partial Regex Letters();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex Cjk();
}
