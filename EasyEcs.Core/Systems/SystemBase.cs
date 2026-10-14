namespace EasyEcs.Core.Systems;

/// <summary>
/// Base class of all systems. A system's update is <see cref="OnUpdate"/> followed by its <c>[Each]</c> methods
/// (generated) in declaration order. All hooks run synchronously on the thread calling <see cref="World.Update"/>.
/// </summary>
public abstract class SystemBase
{
    /// <summary>The world this system belongs to (set before <see cref="OnCreate"/>).</summary>
    public World World { get; internal set; }

    /// <summary>Execution order: lower runs first. Systems with equal priority form a bucket.</summary>
    public virtual int Priority => 0;

    /// <summary>Run every N-th world update (1 = every update).</summary>
    public virtual int UpdateInterval => 1;

    /// <summary>Disabled systems are skipped by <see cref="World.Update"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Called once when the system is added to a world.</summary>
    protected virtual void OnCreate()
    {
    }

    /// <summary>Called every update before the system's <c>[Each]</c> methods.</summary>
    protected virtual void OnUpdate()
    {
    }

    /// <summary>Called once when the system is removed or the world is disposed.</summary>
    protected virtual void OnDestroy()
    {
    }

    /// <summary>Runs the <c>[Each]</c> methods; implemented by EasyEcs.Generator. Do not override by hand.</summary>
    protected virtual void RunEach()
    {
    }

    // Entry points used by World (hooks stay protected so user overrides are plain `protected override`).
    internal void Create() => OnCreate();

    internal void Update()
    {
        OnUpdate();
        RunEach();
    }

    internal void Destroy() => OnDestroy();
}
