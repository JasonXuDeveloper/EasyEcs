using System.Diagnostics;

namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: tells the host loop when the next simulation tick is due, so the world
/// updates at a fixed rate however long each tick takes.
/// </summary>
public sealed class FixedTick(int ticksPerSecond)
{
    private readonly TimeSpan _length = TimeSpan.FromSeconds(1.0 / ticksPerSecond);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _next = TimeSpan.Zero;

    /// <summary>Time since the loop started.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// True when a tick is due (and schedules the one after it). Otherwise false, with how long
    /// to wait in <paramref name="wait"/>.
    /// </summary>
    public bool IsDue(out TimeSpan wait)
    {
        TimeSpan now = _clock.Elapsed;
        if (now < _next)
        {
            wait = _next - now;
            return false;
        }

        _next += _length;
        wait = TimeSpan.Zero;
        return true;
    }
}
