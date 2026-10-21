namespace GameLoop.Host;

/// <summary>
/// Plumbing, not ECS: prints each check as it is made and remembers whether all of them passed,
/// so the process can exit with 1 when one fails.
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
