using System;

namespace EasyEcs;

/// <summary>
/// Logic that runs on a world. Lifecycle: <see cref="OnCreate"/> when added, then every update <see cref="OnUpdate"/>
/// followed by the <see cref="ForEachAttribute">[ForEach]</see> methods, and <see cref="OnDestroy"/> when removed or
/// when the world is disposed. Everything runs synchronously within <see cref="World.Update()"/> (or
/// <see cref="World.Update{TGroup}"/> for systems in a <see cref="UpdateInGroupAttribute{TGroup}">group</see>): on the
/// calling thread, or with <see cref="WorldOptions.ParallelSystems"/> on a thread-pool thread next to systems whose
/// <see cref="SystemAccess">access</see> does not conflict.
/// </summary>
public abstract class EcsSystem
{
    /// <summary>The world this system belongs to (set before <see cref="OnCreate"/>).</summary>
    public World World { get; internal set; }

    // The system types this one runs before and after ([UpdateBefore<T>] / [UpdateAfter<T>]), read when it is added.
    internal Type[] RunsBefore = [], RunsAfter = [];

    /// <summary>Run every N-th world update (1 = every update); see <see cref="UpdatePhase"/>.</summary>
    public virtual int UpdateInterval => 1;

    /// <summary>
    /// Which of the <see cref="UpdateInterval"/> updates the system runs on (0 to interval - 1): systems with the same
    /// interval and different phases spread their work over the updates instead of all running on the same one.
    /// </summary>
    public virtual int UpdatePhase => 0;

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
    /// <see cref="UpdateInterval"/> or while disabled do not count, a run that throws does). A
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

    // The generated code of the system's type and its base types, base first (SystemCode.For).
    private SystemCode[] _code = [];

    // Entry points used by World (hooks stay protected so user overrides are plain `protected override`).
    internal void Create() => OnCreate();

    internal void DeclareAccess()
    {
        Access = default;
        _code = SystemCode.For(GetType(), out bool own);
        foreach (var code in _code) code.Declare(ref Access);
        if (!own) Access.RunsAlone(); // no generated code of its own: it may override OnUpdate, or have no [ForEach]
    }

    internal void Update()
    {
        OnUpdate();
        var code = _code;
        for (int i = 0; i < code.Length; i++) code[i].Run(this);
    }

    internal void Destroy() => OnDestroy();
}
