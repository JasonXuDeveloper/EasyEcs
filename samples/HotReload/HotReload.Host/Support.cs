namespace HotReload.Host;

/// <summary>
/// Plumbing, not ECS: collects the result of each check and prints a line per check.
/// </summary>
public sealed class Checks
{
    public bool AllPassed { get; private set; } = true;

    public void That(string what, bool passed)
    {
        string mark = passed ? "ok  " : "FAIL";
        Console.WriteLine($"  {mark} {what}");
        AllPassed &= passed;
    }
}
