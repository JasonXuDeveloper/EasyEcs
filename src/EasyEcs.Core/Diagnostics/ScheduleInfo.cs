using System;
using System.Collections.Generic;
using System.Text;

namespace EasyEcs;

/// <summary>
/// How a world runs its systems (<see cref="World.DescribeSchedule"/>): every group's stages, what each system reads and
/// writes, why a system waits for an earlier stage, and which conflicting systems are ordered by nothing but their
/// registration order. A description in names, for reading (<see cref="ToString"/>) or drawing
/// (<see cref="ToMermaid"/>); the scheduler does not read it back.
/// </summary>
/// <param name="Groups">The groups that hold systems, the default group first.</param>
public sealed record ScheduleInfo(GroupInfo[] Groups)
{
    /// <summary>The schedule as indented text: per group its stages and systems, then its registration-only pairs.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var group in Groups)
        {
            text.Append("Group ").Append(group.Name).Append('\n');
            for (int s = 0; s < group.Stages.Length; s++)
            {
                text.Append("  Stage ").Append(s + 1).Append('\n');
                foreach (var system in group.Stages[s].Systems)
                {
                    text.Append("    ").Append(system.Name);
                    if (system.RunsAlone) text.Append(" (runs alone)");
                    Names(text, "reads", system.Reads);
                    Names(text, "writes", system.Writes);
                    Names(text, "reads singletons", system.ReadsSingletons);
                    Names(text, "writes singletons", system.WritesSingletons);
                    text.Append('\n');
                    foreach (var loop in system.Loops) text.Append("      loop ").Append(loop).Append('\n');
                    if (system.FusedWith != null)
                        text.Append("      one walk with the next system, ").Append(system.FusedWith).Append(", when both run\n");
                    foreach (var wait in system.After)
                    {
                        text.Append("      after ").Append(wait.System).Append(": ");
                        if (wait.Ordered) text.Append(wait.Conflicts.Length == 0 ? "ordered" : "ordered, ");
                        text.Append(string.Join(", ", wait.Conflicts)).Append('\n');
                    }
                }
            }

            foreach (var pair in group.RegistrationOrdered)
                text.Append("  Registration order only: ").Append(pair.First).Append(" before ").Append(pair.Second)
                    .Append(" (").Append(string.Join(", ", pair.Conflicts)).Append(")\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The schedule as a mermaid flowchart: a subgraph per group holding a subgraph per stage, a node per system (its
    /// reads and writes), a solid arrow per wait (labelled with the conflicting data, or "ordered") and a dotted arrow
    /// per registration-only pair.
    /// </summary>
    public string ToMermaid()
    {
        var text = new StringBuilder("flowchart TD\n");
        for (int g = 0; g < Groups.Length; g++)
        {
            var group = Groups[g];
            var ids = new Dictionary<string, string>();
            text.Append("  subgraph g").Append(g).Append("[\"").Append(Escape(group.Name)).Append("\"]\n");
            for (int s = 0; s < group.Stages.Length; s++)
            {
                text.Append("    subgraph g").Append(g).Append('s').Append(s).Append("[\"Stage ").Append(s + 1).Append("\"]\n");
                foreach (var system in group.Stages[s].Systems)
                {
                    string id = "g" + g + "n" + ids.Count;
                    ids[system.Name] = id;
                    text.Append("      ").Append(id).Append("[\"").Append(Escape(system.Name));
                    if (system.RunsAlone) text.Append("<br/>runs alone");
                    Lines(text, "reads", system.Reads, system.ReadsSingletons);
                    Lines(text, "writes", system.Writes, system.WritesSingletons);
                    text.Append("\"]\n");
                }

                text.Append("    end\n");
            }

            text.Append("  end\n");
            foreach (var stage in group.Stages)
            foreach (var system in stage.Systems)
            foreach (var wait in system.After)
            {
                string label = wait.Ordered && wait.Conflicts.Length == 0 ? "ordered" : string.Join(", ", wait.Conflicts);
                text.Append("  ").Append(ids[wait.System]).Append(" -->|\"").Append(Escape(label)).Append("\"| ").Append(ids[system.Name]).Append('\n');
            }

            foreach (var pair in group.RegistrationOrdered)
                text.Append("  ").Append(ids[pair.First]).Append(" -. \"registration order\" .-> ").Append(ids[pair.Second]).Append('\n');
        }

        return text.ToString();
    }

    private static void Names(StringBuilder text, string what, string[] names)
    {
        if (names.Length > 0) text.Append(", ").Append(what).Append(' ').Append(string.Join(", ", names));
    }

    private static void Lines(StringBuilder text, string what, string[] components, string[] singletons)
    {
        if (components.Length + singletons.Length == 0) return;
        text.Append("<br/>").Append(what).Append(": ").Append(Escape(string.Join(", ", components)));
        if (components.Length > 0 && singletons.Length > 0) text.Append(", ");
        if (singletons.Length > 0) text.Append(Escape(string.Join(", ", singletons))).Append(" (singleton)");
    }

    /// <summary>Mermaid entity codes for the characters a quoted label cannot hold.</summary>
    private static string Escape(string text) => text.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;");

    /// <summary>A type's name as reports show it: no namespace, generic arguments in angle brackets (<c>Regen&lt;Health&gt;</c>).</summary>
    internal static string NameOf(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        if (!type.IsGenericType) return name;
        var arguments = type.GetGenericArguments();
        var names = new string[arguments.Length];
        for (int i = 0; i < arguments.Length; i++) names[i] = NameOf(arguments[i]);
        return name + "<" + string.Join(", ", names) + ">";
    }
}

/// <summary>The systems of one group in stages (<see cref="ScheduleInfo"/>).</summary>
/// <param name="Name">The group type's name, or "Default" for the default group.</param>
/// <param name="Stages">The stages in execution order: each one's systems may run at the same time
/// (<see cref="WorldOptions.ParallelSystems"/>).</param>
/// <param name="RegistrationOrdered">Pairs of systems whose access conflicts and that no chain of
/// <see cref="UpdateBeforeAttribute{TSystem}"/> / <see cref="UpdateAfterAttribute{TSystem}"/> orders: they run in
/// registration order, so registering them the other way round changes what the later one sees.</param>
public sealed record GroupInfo(string Name, StageInfo[] Stages, SystemPair[] RegistrationOrdered);

/// <summary>One stage of a group: systems with no ordering and no conflict between them.</summary>
/// <param name="Systems">The stage's systems in execution order (when the stage runs on one thread).</param>
public sealed record StageInfo(SystemInfo[] Systems);

/// <summary>A system of a schedule and the data it declares (<see cref="SystemAccess"/>).</summary>
/// <param name="Name">The system type's name.</param>
/// <param name="Reads">Components its [ForEach] methods read (<c>in</c> parameters, filters of [Changed&lt;T&gt;]).</param>
/// <param name="Writes">Components its [ForEach] methods write (<c>ref</c> parameters).</param>
/// <param name="ReadsSingletons">Singletons it reads.</param>
/// <param name="WritesSingletons">Singletons it writes.</param>
/// <param name="RunsAlone">It may touch anything (an OnUpdate method, an Entity or World parameter...): it never
/// shares a stage.</param>
/// <param name="After">Why it is not in an earlier stage: the systems of the stage just before its own that it must
/// follow (empty in the first stage).</param>
public sealed record SystemInfo(string Name, string[] Reads, string[] Writes, string[] ReadsSingletons, string[] WritesSingletons,
    bool RunsAlone, StageWait[] After)
{
    /// <summary>
    /// How each of its [ForEach] methods runs, one line per method: a vector loop and its forms ("Move: vector (Vector512
    /// or Vector&lt;float&gt;), flat"), or one row at a time and why ("Spawn: scalar (it takes the Entity)"), as the
    /// generator decided (the reasons of EECS055).
    /// </summary>
    public string[] Loops { get; init; } = [];

    /// <summary>
    /// The system after it that it runs as one walk with (pass fusion: each row of their shared filter is loaded once
    /// for both) whenever both are enabled and due, or null. Only one thread running the group and
    /// <see cref="WorldOptions.MeasureSystems"/> off fuse systems.
    /// </summary>
    public string FusedWith { get; init; }
}

/// <summary>A system a later one waits for (<see cref="SystemInfo.After"/>).</summary>
/// <param name="System">The earlier system's name.</param>
/// <param name="Ordered">An <see cref="UpdateBeforeAttribute{TSystem}"/> / <see cref="UpdateAfterAttribute{TSystem}"/>
/// between the two orders them.</param>
/// <param name="Conflicts">The data they conflict on: component names, singleton names marked "(singleton)", or
/// "runs alone".</param>
public sealed record StageWait(string System, bool Ordered, string[] Conflicts);

/// <summary>Two conflicting systems in execution order, ordered only by registration (<see cref="GroupInfo.RegistrationOrdered"/>).</summary>
/// <param name="First">The system that runs first.</param>
/// <param name="Second">The system that runs second.</param>
/// <param name="Conflicts">The data they conflict on (as in <see cref="StageWait.Conflicts"/>).</param>
public sealed record SystemPair(string First, string Second, string[] Conflicts);
