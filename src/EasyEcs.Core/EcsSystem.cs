using System;
using System.Collections.Generic;

namespace EasyEcs;

/// <summary>
/// Logic that runs on a world. Lifecycle: <see cref="OnCreate"/> when added, then every update <see cref="OnUpdate"/>
/// followed by the <see cref="ForEachAttribute">[ForEach]</see> methods, and <see cref="OnDestroy"/> when removed or
/// when the world is disposed. Everything runs synchronously within <see cref="World.Update()"/> (or
/// <see cref="World.Update{TGroup}"/> for systems in a <see cref="UpdateInGroupAttribute{TGroup}">group</see>): on the
/// calling thread, or with <see cref="WorldOptions.ParallelSystems"/> on a thread-pool thread next to systems whose
/// <see cref="SystemAccess">access</see> does not conflict.
/// </summary>
public abstract unsafe class EcsSystem
{
    /// <summary>The world this system belongs to (set before <see cref="OnCreate"/>).</summary>
    public World World { get; internal set; }

    // The system types this one runs before and after ([UpdateBefore<T>] / [UpdateAfter<T>]), read when it is added.
    internal Type[] RunsBefore = [], RunsAfter = [];

    // [UpdateInterval(n, Phase = p)] of the type or its nearest base that has one (SystemCode.For): run on the updates
    // whose count modulo Interval is Phase. 1 and 0 without one: every update.
    internal int Interval = 1, Phase;

    /// <summary>Index of the system's group in its world (0 = default group).</summary>
    internal int Group;

    /// <summary>
    /// Disabled systems are skipped by <see cref="World.Update()"/> and <see cref="World.Update{TGroup}()"/>. Enabling
    /// a system clears its <see cref="Error"/>.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value) Error = null;
        }
    }

    private bool _enabled = true;

    /// <summary>
    /// The exception that switched this system off under <see cref="SystemErrorPolicy.Disable"/>, until it is enabled
    /// again; null otherwise.
    /// </summary>
    public Exception Error { get; private set; }

    /// <summary>Switch the system off for <paramref name="error"/> (<see cref="SystemErrorPolicy.Disable"/>).</summary>
    internal void Fail(Exception error)
    {
        _enabled = false;
        Error = error;
    }

    /// <summary>
    /// The world's <see cref="World.ChangeTick"/> when this system last ran (0 before its first run): its
    /// <c>[Changed&lt;T&gt;]</c> methods select rows written after that tick, so not their own writes of that run.
    /// </summary>
    public ulong LastRunTick { get; internal set; }

    /// <summary>
    /// How many times the system has run before the current run (0 during its first run; runs skipped by
    /// <see cref="UpdateIntervalAttribute">[UpdateInterval]</see> or while disabled do not count, a run that throws does). A
    /// <c>[ForEach(Slices = N)]</c> method visits slice <c>RunCount % N</c> of its rows, so <see cref="OnUpdate"/> can
    /// tell which slice this run visits.
    /// </summary>
    public long RunCount { get; internal set; }

    /// <summary>
    /// Run count, time, allocated bytes and visited entities of this system; recorded only with
    /// <see cref="WorldOptions.MeasureSystems"/>.
    /// </summary>
    public SystemStats Stats { get; internal set; }

    // Entities the [ForEach] methods of the current measured run visited (SystemCode.Visit); one system runs on one
    // thread at a time, so a plain field.
    internal long Visited;

    /// <summary>Called once when the system is added to a world.</summary>
    protected virtual void OnCreate()
    {
    }

    /// <summary>Called every update before the system's [ForEach] methods.</summary>
    protected virtual void OnUpdate()
    {
    }

    /// <summary>Called once when the system is removed or the world is disposed.</summary>
    protected virtual void OnDestroy()
    {
    }

    /// <summary>What the system reads and writes, declared once by <see cref="World.AddSystem(EcsSystem, bool)"/>.</summary>
    internal SystemAccess Access;

    // The generated code of the system's type and its base types, base first (SystemCode.For); _run holds their Update
    // functions, called directly each update (no instance, no virtual call).
    private SystemCode.Entry[] _code = [];
    private delegate*<EcsSystem, void>[] _run = [];

    // Whether OnUpdate is called: the generated code says whether the type or a base overrides it; a type without
    // generated code of its own may override it unseen.
    private bool _callsOnUpdate = true;

    /// <summary>How each [ForEach] method of the system's type (and its base types, base first) runs (<see cref="SystemInfo.Loops"/>).</summary>
    internal string[] Loops()
    {
        var loops = new List<string>();
        foreach (var code in _code) loops.AddRange(code.Loops);
        return loops.ToArray();
    }

    // Entry points used by World (hooks stay protected so user overrides are plain `protected override`).
    internal void Create() => OnCreate();

    internal void DeclareAccess()
    {
        Access = default;
        _code = SystemCode.For(GetType(), out bool own);
        _run = new delegate*<EcsSystem, void>[_code.Length];
        Interval = 1;
        Phase = 0;
        for (int i = 0; i < _code.Length; i++)
        {
            var code = _code[i];
            _run[i] = code.Update;
            code.Access(ref Access);
            // Base first: the nearest type with [UpdateInterval] wins.
            if (code.Interval > 0) (Interval, Phase) = (code.Interval, code.Phase);
        }

        // The type's own code walks its base types too, so its flag covers the chain.
        _callsOnUpdate = !own || _code[^1].OverridesOnUpdate;
        if (!own) Access.RunAlone(); // no generated code of its own: it may override OnUpdate, or have no [ForEach]
    }

    internal void Update()
    {
        if (_callsOnUpdate) OnUpdate();
        var run = _run;
        for (int i = 0; i < run.Length; i++) run[i](this);
    }

    internal void Destroy() => OnDestroy();
}

/// <summary>
/// Run the system (and derived systems without their own) on every <paramref name="interval"/>-th update of its group
/// only: the updates whose count (<see cref="World.DefaultUpdateCount"/>, <see cref="World.UpdateCount{TGroup}"/>)
/// modulo the interval is <see cref="Phase"/>. Systems with the same interval and different phases spread their work
/// over the updates instead of all running on the same one. The generator writes the values into the system's
/// registration; the interval is 1 or more and the phase 0 to interval - 1 (EECS083).
/// <code>
/// [UpdateInterval(4, Phase = 1)]   // updates 1, 5, 9, ...
/// public class Regrowth : EcsSystem { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class UpdateIntervalAttribute(int interval) : Attribute
{
    /// <summary>Run every N-th update of the system's group (1 = every update).</summary>
    public int Interval { get; } = interval;

    /// <summary>Which of the <see cref="Interval"/> updates the system runs on, 0 to interval - 1. Default 0.</summary>
    public int Phase { get; init; }
}
