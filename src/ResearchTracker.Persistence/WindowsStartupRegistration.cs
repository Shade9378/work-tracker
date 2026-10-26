using Microsoft.Win32;

namespace ResearchTracker.Persistence;

public interface IStartupRegistration
{
    void SetEnabled(bool enabled);
}

public sealed class WindowsStartupRegistration(string executablePath) : IStartupRegistration
{
    public const string ValueName = "WorkTracker";
    public const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string Command(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n']) >= 0 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an absolute executable path for Windows startup.");
        return "\"" + path + "\"";
    }
    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) { if (enabled) throw new PlatformNotSupportedException("Automatic startup requires Windows."); return; }
        var command = enabled ? Command(executablePath) : null;
        if (enabled && !File.Exists(executablePath)) throw new FileNotFoundException("Work Tracker executable could not be found.", executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (enabled) key.SetValue(ValueName, command!, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
