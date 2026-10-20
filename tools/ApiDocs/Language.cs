namespace ApiDocs;

/// <summary>The words of one language of the reference: URLs, file names, headings and the generated sentences.</summary>
public sealed record Language
{
    public required string Code { get; init; }               // "en", "zh": the URL prefix
    public required string Suffix { get; init; }             // "", ".zh": page.mdx, page.zh.mdx, meta.zh.json
    public required string ReferenceTitle { get; init; }
    public required string IndexDescription { get; init; }
    public required string IndexIntro { get; init; }
    public required string TypeColumn { get; init; }
    public required string SummaryColumn { get; init; }
    public required Dictionary<string, string> Kinds { get; init; }      // Classes -> heading of the index section
    public required Dictionary<string, string> KindWords { get; init; }  // class -> the word in the page's seoTitle
    public required Dictionary<string, string> Sections { get; init; }   // Constructors, Fields... -> heading
    public required Dictionary<string, string> Labels { get; init; }     // Parameters, Returns... -> label
    public required Func<string, string> NestedIn { get; init; }
    public required Func<int, int, string, string> Arities { get; init; } // first arity, last arity, shown type
    public required Func<int, string> Overloads { get; init; }
    public required string ComplexityLabel { get; init; }        // followed by the cost
    public required Func<string, string> Inline { get; init; }   // a bold label before its text: Returns, Value
    public required string Colon { get; init; }                  // between a list item's name and its text
    public required Func<string, string, string> SeoTitle { get; init; } // title, kind word
    public required Func<string, string, string, string> Description { get; init; } // first sentence, title, kind word

    public string Url => $"/{Code}/docs/reference/";

    public static Language English { get; } = new()
    {
        Code = "en",
        Suffix = "",
        ReferenceTitle = "API reference",
        IndexDescription = "Every public type of EasyEcs.Core with its members, generated from the XML documentation.",
        IndexIntro = "Everything is in `namespace EasyEcs`. These pages are generated from the XML documentation of `EasyEcs.Core`; " +
                     "the members the generator writes for your own components (`entity.Position`, `world.Clock`, `SetDead()`) are in " +
                     "[Entities and components](/en/docs/guide/entities-and-components#generated-members), and the complexity of every call in " +
                     "[API and complexity](/en/docs/api).",
        TypeColumn = "Type",
        SummaryColumn = "Summary",
        Kinds = new() { ["Classes"] = "Classes", ["Structs"] = "Structs", ["Interfaces"] = "Interfaces", ["Enums"] = "Enums", ["Delegates"] = "Delegates" },
        KindWords = new() { ["class"] = "class", ["struct"] = "struct", ["interface"] = "interface", ["enum"] = "enum", ["delegate"] = "delegate" },
        Sections = new()
        {
            ["Constructors"] = "Constructors", ["Fields"] = "Fields", ["Properties"] = "Properties", ["Indexers"] = "Indexers",
            ["Methods"] = "Methods", ["Extension members"] = "Extension members", ["Operators"] = "Operators", ["Events"] = "Events",
        },
        Labels = new()
        {
            ["Type parameters"] = "Type parameters", ["Parameters"] = "Parameters", ["Returns"] = "Returns", ["Value"] = "Value",
            ["Remarks"] = "Remarks", ["Exceptions"] = "Exceptions",
        },
        NestedIn = type => $"Nested in {type}.",
        Arities = (first, last, shown) => $"One type per number of components, {first} to {last} (generated from one template); the members below are those of each, shown for {shown}.",
        Overloads = count => $"One overload per number of components, 1 to {count} (generated from one template); this is the text of the two-component one.",
        ComplexityLabel = "**Complexity** ([API table](/en/docs/api)): ",
        Inline = label => $"**{label}** ",
        Colon = ": ",
        SeoTitle = (title, kind) => $"{title} {kind} – EasyEcs C# API",
        Description = Mdx.Description,
    };

    public static Language Chinese { get; } = new()
    {
        Code = "zh",
        Suffix = ".zh",
        ReferenceTitle = "API 参考",
        IndexDescription = "EasyEcs.Core 的全部公开类型及其成员、参数与复杂度，由 XML 文档注释生成，与源码保持同步。",
        IndexIntro = "所有类型都在 `namespace EasyEcs` 里。这些页面由 `EasyEcs.Core` 的 XML 文档注释生成；生成器为你自己的组件写的成员" +
                     "（`entity.Position`、`world.Clock`、`SetDead()`）见[实体与组件](/zh/docs/guide/entities-and-components#generated-members)，" +
                     "每个调用的复杂度见 [API 与复杂度](/zh/docs/api)。",
        TypeColumn = "类型",
        SummaryColumn = "说明",
        Kinds = new() { ["Classes"] = "类", ["Structs"] = "结构体", ["Interfaces"] = "接口", ["Enums"] = "枚举", ["Delegates"] = "委托" },
        KindWords = new() { ["class"] = "类", ["struct"] = "结构体", ["interface"] = "接口", ["enum"] = "枚举", ["delegate"] = "委托" },
        Sections = new()
        {
            ["Constructors"] = "构造函数", ["Fields"] = "字段", ["Properties"] = "属性", ["Indexers"] = "索引器",
            ["Methods"] = "方法", ["Extension members"] = "扩展成员", ["Operators"] = "运算符", ["Events"] = "事件",
        },
        Labels = new()
        {
            ["Type parameters"] = "类型参数", ["Parameters"] = "参数", ["Returns"] = "返回值", ["Value"] = "值",
            ["Remarks"] = "备注", ["Exceptions"] = "异常",
        },
        NestedIn = type => $"嵌套在 {type} 里。",
        Arities = (first, last, shown) => $"组件数量从 {first} 到 {last}，每个数量一个类型，都由同一个模板生成；下面的成员每个类型都有，以 {shown} 为例。",
        Overloads = count => $"组件数量从 1 到 {count}，每个数量一个重载，都由同一个模板生成；这里是两个组件那个重载的说明。",
        ComplexityLabel = "**复杂度**（[API 表](/zh/docs/api)）：",
        Inline = label => $"**{label}**：",
        Colon = "：",
        SeoTitle = (title, kind) => $"{title} {kind} – EasyEcs C# API",
        Description = Mdx.DescriptionZh,
    };
}
