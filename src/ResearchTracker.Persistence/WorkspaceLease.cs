namespace ResearchTracker.Persistence;

/// <summary>Shared writer exclusion for the desktop session and short-lived CLI/MCP commands.</summary>
public static class WorkspaceLease
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("RESEARCH_TRACKER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResearchTracker");
    public static FileStream Acquire(string root)
    {
        Directory.CreateDirectory(root);
        try { return new FileStream(Path.Combine(root, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Workspace is busy. Close the Work Tracker desktop window or retry after the other CLI/MCP write finishes. Read-only commands remain available.", ex); }
    }
}
