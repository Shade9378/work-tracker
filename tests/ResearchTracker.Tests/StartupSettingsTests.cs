using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class StartupSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerStartup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Registration : IStartupRegistration
    {
        public readonly List<bool> Changes = [];
        public bool Fail { get; set; }
        public void SetEnabled(bool enabled) { if (Fail) throw new UnauthorizedAccessException("Startup registration denied"); Changes.Add(enabled); }
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static SettingsWindow Settings(MainWindow owner)
    {
        Click(owner.FindControl<Button>("SettingsButton")!); return Assert.IsType<SettingsWindow>(Assert.Single(owner.OwnedWindows));
    }
    [AvaloniaFact] public void StartupDefaultsOffAndOnlySavedChangesRegisterOrUnregisterIt()
    {
        var registration = new Registration(); var store = new AppearanceStore(root); var window = new MainWindow(new MainViewModel(new WorkspaceStore(root)), registration); window.Show();
        try
        {
            Assert.False(new AppearancePreferences().StartWithWindows); Assert.Empty(registration.Changes);
            var dialog = Settings(window); var toggle = dialog.GetVisualDescendants().OfType<CheckBox>().Single(); Assert.False(toggle.IsChecked); toggle.IsChecked = true;
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel"))); Assert.Empty(registration.Changes); Assert.False(store.Load().StartWithWindows);
            dialog = Settings(window); dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true; Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings")); Assert.Equal(new[] { true }, registration.Changes); Assert.True(store.Load().StartWithWindows);
            dialog = Settings(window); Assert.True(dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked); dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = false; Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings")); Assert.Equal(new[] { true, false }, registration.Changes); Assert.False(store.Load().StartWithWindows);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void FailedStartupRegistrationKeepsPreviousSettingsAndShowsError()
    {
        var registration = new Registration { Fail = true }; var window = new MainWindow(new MainViewModel(new WorkspaceStore(root)), registration); window.Show();
        try
        {
            var dialog = Settings(window); dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true; Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings"));
            Assert.Single(window.OwnedWindows); Assert.False(new AppearanceStore(root).Load().StartWithWindows); Assert.Contains("denied", dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SettingsError").Text); dialog.Close();
        }
        finally { window.Close(); }
    }
    [Fact] public void StartupCommandQuotesPathsAndRejectsRelativeOrInjectedCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "Work Tracker", "WorkTracker.Desktop.exe"); Assert.Equal("\"" + path + "\"", WindowsStartupRegistration.Command(path));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.Command("relative.exe")); Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.Command(path + "\" --injected"));
    }
}
