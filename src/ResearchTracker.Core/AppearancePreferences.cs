namespace ResearchTracker.Core;

public sealed record AppearancePreferences
{
    public string Theme { get; init; } = "Dark";
    public bool StartWithWindows { get; init; }
    public void Validate()
    {
        if (Theme is not "Dark" and not "Light") throw new ArgumentException("Choose Dark or Light.");
    }
}
