namespace MultiWorld;

/// <summary>Plumbing, not ECS: command-line options.</summary>
static class Support
{
    /// <summary>The integer after <paramref name="name"/> in <paramref name="args"/>, or <paramref name="fallback"/>.</summary>
    public static int Option(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
    }
}
