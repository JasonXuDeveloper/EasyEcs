using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace ApiDocs;

/// <summary>The XML documentation file of an assembly, by documentation id (<c>M:EasyEcs.World.Update</c>).</summary>
public sealed class XmlDocs : DocumentationProvider
{
    private readonly Dictionary<string, string> _members = [];

    public XmlDocs(string path)
    {
        foreach (var member in XDocument.Load(path, LoadOptions.PreserveWhitespace).Descendants("member"))
            _members[(string)member.Attribute("name")!] = member.ToString(SaveOptions.DisableFormatting);
    }

    protected override string GetDocumentationForSymbol(string documentationMemberID, CultureInfo preferredCulture,
        CancellationToken cancellationToken = default) => _members.GetValueOrDefault(documentationMemberID, "");

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => _members.Count;
}
