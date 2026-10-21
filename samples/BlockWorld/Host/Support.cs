namespace BlockWorld;

// Not part of the lesson: command-line options.
static class Support
{
    public static int Option(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
    }
}
