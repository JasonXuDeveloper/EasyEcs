using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EasyEcs.Generator;

/// <summary>
/// Pass fusion (loop fusion, FlashAttention's idea): consecutive [ForEach] methods of one system that visit the same
/// rows run as one walk, so each row is loaded into cache once. Two passes fuse only when the walk gives the same result
/// as the passes one after the other: the same filter (table set and row filter), the same schedule settings, no
/// structural change, no read of another row the first writes (<c>Lookup&lt;T&gt;</c>), no per-pass result the second
/// reads (a singleton, <c>Reduce&lt;T&gt;</c>, <c>Emit&lt;T&gt;</c>), no change filter on what the first writes.
/// <see cref="FusionSide.Of"/> reads a method once; <see cref="Problem"/> is the rule, which the generator follows
/// (SystemGenerator.Fusion.cs) and the analyzer reports (EECS093), so "not fused" is exactly "reported".
/// </summary>
internal static class Fusion
{
    /// <summary>
    /// Why <paramref name="second"/> cannot run in one walk with the methods of <paramref name="group"/> (consecutive,
    /// already fused, in order), or null. Each rule is checked against every method of the group: a Lookup of the first
    /// method sees the writes of a later one in the walk as much as of the next.
    /// </summary>
    public static string? Problem(IReadOnlyList<FusionSide> group, FusionSide second)
    {
        if (second.Blocker != null) return second.Blocker;
        foreach (var first in group)
            if (Problem(first, second) is { } problem)
                return problem;
        var query = new HashSet<string>();
        foreach (var side in group) query.UnionWith(side.Query);
        query.UnionWith(second.Query);
        return query.Count > 9 ? "together they take more than 9 components" : null;
    }

    /// <summary>Why <paramref name="first"/> and <paramref name="second"/> (run in this order) cannot share one walk, or null.</summary>
    public static string? Problem(FusionSide first, FusionSide second)
    {
        if (first.Blocker != null) return first.Blocker;
        if (second.Blocker != null) return second.Blocker;
        if (first.Parallel != second.Parallel)
            return first.Parallel ? $"{first.Name} runs in parallel and {second.Name} does not" : $"{second.Name} runs in parallel and {first.Name} does not";
        if (first.ChunkRows != second.ChunkRows) return $"their ChunkRows differ ({first.ChunkRows} and {second.ChunkRows})";
        if (first.Slices != second.Slices) return $"their Slices differ ({first.Slices} and {second.Slices})";
        if (FilterDifference(first, second) is { } filter) return filter;
        if (Shared(first.Writes, second.Since) is { } changed)
            return $"{second.Name} filters on changes to {Short(changed)}, which {first.Name} writes";
        if (Shared(first.Writes, second.Remote) is { } read)
            return $"{second.Name} reads {Short(read)} of other entities, which {first.Name} writes: in one walk it would see some rows written and others not";
        if (Shared(second.Writes, first.Remote) is { } early)
            return $"{first.Name} reads {Short(early)} of other entities, which {second.Name} writes: in one walk it would see some rows written and others not";
        if (Shared(first.SingletonWrites, second.SingletonReads.Concat(second.SingletonWrites)) is { } result)
            return $"{second.Name} uses singleton {Short(result)}, which {first.Name} writes once per pass";
        if (Shared(second.SingletonWrites, first.SingletonReads) is { } later)
            return $"{first.Name} reads singleton {Short(later)}, which {second.Name} writes";
        if (Shared(first.Scratches, second.Scratches) is { } scratch) return $"both use Scratch<{Short(scratch)}>";
        return null;
    }

    /// <summary>
    /// How the filters of two methods differ, or null when they select the same rows: the components the rows need
    /// (queried and [With&lt;T&gt;]), [Without&lt;T&gt;], [WithAny&lt;T&gt;] and the change filters.
    /// </summary>
    private static string? FilterDifference(FusionSide first, FusionSide second)
    {
        var parts = new List<string>();
        Describe(first.Name, "needs", first.With, second.With);
        Describe(second.Name, "needs", second.With, first.With);
        Describe(first.Name, "excludes", first.Without, second.Without);
        Describe(second.Name, "excludes", second.Without, first.Without);
        Describe(first.Name, "takes any of", first.WithAny, second.WithAny);
        Describe(second.Name, "takes any of", second.WithAny, first.WithAny);
        if (!first.Filters.SequenceEqual(second.Filters)) parts.Add("their change filters differ");
        return parts.Count == 0 ? null : "their filters differ: " + string.Join("; ", parts);

        void Describe(string name, string verb, EquatableArray<string> mine, EquatableArray<string> theirs)
        {
            var only = mine.Where(t => !theirs.Contains(t)).Select(Short).ToArray();
            if (only.Length > 0) parts.Add($"only {name} {verb} {string.Join(", ", only)}");
        }
    }

    /// <summary>
    /// The number of component types the filters of two methods differ by (null when anything else differs): 1 is a near
    /// miss EECS093 reports, a filter attribute away.
    /// </summary>
    public static int FilterDistance(FusionSide first, FusionSide second) =>
        Count(first.With, second.With) + Count(first.Without, second.Without) + Count(first.WithAny, second.WithAny)
        + (first.Filters.SequenceEqual(second.Filters) ? 0 : 2);

    private static int Count(EquatableArray<string> a, EquatableArray<string> b) =>
        a.Count(t => !b.Contains(t)) + b.Count(t => !a.Contains(t));

    private static string? Shared(IEnumerable<string> a, IEnumerable<string> b)
    {
        foreach (var x in a)
            if (b.Contains(x))
                return x;
        return null;
    }

    /// <summary>A type as a message names it: <c>global::Game.Health</c> as <c>Health</c>.</summary>
    public static string Short(string type) => System.Text.RegularExpressions.Regex.Replace(type, @"(global::)?(\w+\.)*", "");
}

/// <summary>
/// What pass fusion needs to know of one [ForEach] method of a system, read from its symbol once (equatable, part of
/// the generator's model): the rows it visits, what it writes and reads beyond its own row, and why it cannot fuse at
/// all (<see cref="Blocker"/>).
/// </summary>
/// <param name="Name">The method's name, for messages.</param>
/// <param name="Query">Its queried components (the job's type arguments).</param>
/// <param name="With">The components its rows have: queried, [With&lt;T&gt;] (its group's too), sorted.</param>
/// <param name="Filters">Its change filters as text ("Changed:T", "RemovedAny:T"), sorted.</param>
/// <param name="Writes">The components it writes in its row: ref, Span, Mut, a Maybe it may write.</param>
/// <param name="Since">The components its change filters read the ticks of.</param>
/// <param name="Remote">The components it reads of other entities (Lookup&lt;T&gt;).</param>
/// <param name="SingletonReads">The singletons it reads (in).</param>
/// <param name="SingletonWrites">The singletons it writes: ref, Reduce&lt;T&gt;, Emitted&lt;T&gt; of an Emit&lt;T&gt;.</param>
internal sealed record FusionSide(string Name, EquatableArray<string> Query, EquatableArray<string> With,
    EquatableArray<string> Without, EquatableArray<string> WithAny, EquatableArray<string> Filters, EquatableArray<string> Writes,
    EquatableArray<string> Since, EquatableArray<string> Remote, EquatableArray<string> SingletonReads,
    EquatableArray<string> SingletonWrites, EquatableArray<string> Scratches, bool Parallel, int ChunkRows, int Slices, string? Blocker)
{
    /// <summary>
    /// The side of a [ForEach] method of a system (<paramref name="stable"/>: it makes no structural change;
    /// <paramref name="identifies"/>: it uses its Entity only as an identity; <paramref name="parallel"/>: it runs in
    /// parallel), from its declaration and parameters: no body is bound here.
    /// </summary>
    public static FusionSide Of(IMethodSymbol method, bool stable, bool identifies, bool parallel)
    {
        string Fqn(ITypeSymbol t) => t.ToDisplayString(Symbols.Fqn);
        var query = new List<string>();
        var with = new SortedSet<string>(System.StringComparer.Ordinal);
        var without = new SortedSet<string>(System.StringComparer.Ordinal);
        var withAny = new SortedSet<string>(System.StringComparer.Ordinal);
        // One set per [WithAny<..>] / [ChangedAny<..>] / [RemovedAny<..>] attribute: its types are one condition.
        var anySets = new Dictionary<AttributeData, (Symbols.FilterKind Kind, SortedSet<string> Types)>();
        var filters = new SortedSet<string>(System.StringComparer.Ordinal);
        var since = new SortedSet<string>(System.StringComparer.Ordinal);
        var writes = new List<string>();
        var remote = new List<string>();
        var reads = new List<string>();
        var singletons = new List<string>();
        var scratches = new List<string>();
        string? blocker = Symbols.Fusion(method) == 2 ? $"{method.Name} has FusionMode.Off"
            : !stable ? $"{method.Name} may change structure"
            : Symbols.IsConsume(method) ? $"{method.Name} is [Consume]"
            : !identifies ? $"{method.Name} uses its Entity beyond its identity"
            : null;
        string? firstWith = null, firstChanged = null;

        foreach (var p in method.Parameters)
        {
            var kind = Symbols.Classify(p);
            switch (kind)
            {
                case Symbols.ParamKind.Component or Symbols.ParamKind.ComponentSpan or Symbols.ParamKind.Mutable:
                    string type = Fqn(Symbols.QueriedComponent(p)!);
                    query.Add(type);
                    bool written = kind == Symbols.ParamKind.Mutable || p.RefKind == RefKind.Ref
                        || kind == Symbols.ParamKind.ComponentSpan && Symbols.Named(((INamedTypeSymbol)p.Type).ConstructedFrom, "System.Span<T>");
                    if (written) writes.Add(type);
                    break;
                case Symbols.ParamKind.Optional:
                    if (!Symbols.IsReadOnlyMaybe(p.Type)) writes.Add(Fqn(Symbols.MaybeElement(p.Type)!));
                    break;
                case Symbols.ParamKind.View:
                    // Its fields are queried components: ref (or Mut<T>) ones written, ref readonly ones read.
                    if (!Views.IsValid(p.Type)) blocker ??= $"{method.Name} takes an invalid view";
                    else
                        foreach (var field in Views.Fields((INamedTypeSymbol)p.Type))
                        {
                            string component = Fqn(field.Component);
                            query.Add(component);
                            if (field.Writes) writes.Add(component);
                        }
                    break;
                case Symbols.ParamKind.Singleton:
                    (p.RefKind == RefKind.Ref ? singletons : reads).Add(Fqn(p.Type));
                    break;
                case Symbols.ParamKind.Reduction:
                    singletons.Add(Fqn(Symbols.ReduceElement(p.Type)!));
                    break;
                case Symbols.ParamKind.Emit:
                    singletons.Add($"global::EasyEcs.Emitted<{Fqn(Symbols.EmitElement(p.Type)!)}>");
                    break;
                case Symbols.ParamKind.Scratch:
                    scratches.Add(Fqn(Symbols.ScratchElement(p.Type)!));
                    break;
                case Symbols.ParamKind.Lookup:
                    remote.Add(Fqn(Symbols.LookupElement(p.Type)!));
                    break;
                case Symbols.ParamKind.Tile:
                    blocker ??= $"{method.Name} reads its neighbors' tiles";
                    break;
                case Symbols.ParamKind.World:
                    blocker ??= $"{method.Name} takes the World";
                    break;
                case Symbols.ParamKind.Entities:
                    blocker ??= $"{method.Name} takes its EntitySpan";
                    break;
            }
        }

        void Filter(Symbols.FilterKind kind, ITypeSymbol filtered, AttributeData? attribute)
        {
            string type = Fqn(filtered);
            if (kind is Symbols.FilterKind.WithAny or Symbols.FilterKind.ChangedAny or Symbols.FilterKind.RemovedAny && attribute != null)
            {
                if (!anySets.TryGetValue(attribute, out var set)) anySets[attribute] = set = (kind, new SortedSet<string>(System.StringComparer.Ordinal));
                set.Types.Add(type);
                if (kind != Symbols.FilterKind.WithAny) since.Add(type);
                return;
            }

            switch (kind)
            {
                case Symbols.FilterKind.With:
                    firstWith ??= type;
                    with.Add(type);
                    break;
                case Symbols.FilterKind.Without:
                    without.Add(type);
                    break;
                case Symbols.FilterKind.WithAny:
                    withAny.Add(type);
                    break;
                default:
                    if (kind == Symbols.FilterKind.Changed) firstChanged ??= type;
                    filters.Add(kind + ":" + type);
                    since.Add(type);
                    break;
            }
        }

        foreach (var (kind, type, attribute) in Symbols.AttributeFilters(method)) Filter(kind, type, attribute);
        // No component parameter: the job queries the first [With<T>], else the [Changed<T>] type, as the generator does.
        if (query.Count == 0 && (firstWith ?? firstChanged) is { } queried) query.Add(queried);
        foreach (var (kind, type, attribute, _) in Symbols.GroupFilters(method)) Filter(kind, type, attribute);
        foreach (var (kind, types) in anySets.Values)
            (kind == Symbols.FilterKind.WithAny ? withAny : filters).Add((kind == Symbols.FilterKind.WithAny ? "" : kind + ":") + string.Join("|", types));
        with.UnionWith(query);

        return new FusionSide(method.Name, new(query.ToArray()), new(with.ToArray()), new(without.ToArray()), new(withAny.ToArray()),
            new(filters.ToArray()), new(writes.Distinct().ToArray()), new(since.ToArray()), new(remote.Distinct().ToArray()),
            new(reads.Distinct().ToArray()), new(singletons.Distinct().ToArray()), new(scratches.Distinct().ToArray()),
            parallel, parallel ? Symbols.ChunkRows(method) : 0, Symbols.Slices(method), blocker);
    }
}
