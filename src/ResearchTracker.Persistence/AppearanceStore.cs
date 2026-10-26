using System.Text.Json;
using ResearchTracker.Core;

namespace ResearchTracker.Persistence;

public sealed class AppearanceStore(string root)
{
    public string FilePath => Path.Combine(root, "appearance.json");
    public AppearancePreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var preferences = JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(FilePath)) ?? new();
            preferences.Validate(); return preferences;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(AppearancePreferences preferences)
    {
        preferences.Validate(); Directory.CreateDirectory(root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
