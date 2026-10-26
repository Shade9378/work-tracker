using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class AppearanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerAppearance-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void PreferencesRoundTripAndRejectInvalidChanges()
    {
        var store = new AppearanceStore(root); Assert.Equal("Dark", store.Load().Theme);
        store.Save(new AppearancePreferences { Theme = "Light" });
        Assert.Equal(new AppearancePreferences { Theme = "Light" }, new AppearanceStore(root).Load());
        Assert.Throws<ArgumentException>(() => store.Save(new AppearancePreferences { Theme = "Other" }));
        File.WriteAllText(store.FilePath, "{\"Theme\":\"Light\",\"AccentColor\":\"#37A17A\"}");
        Assert.Equal("Light", store.Load().Theme);
        File.WriteAllText(store.FilePath, "bad json"); Assert.Equal(new AppearancePreferences(), store.Load());
    }
    [AvaloniaFact] public void SettingsPersistOnlyThemeAndInspectorUsesOneFifth()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); vm.Selected = vm.Data.Nodes[0]; Dispatcher.UIThread.RunJobs();
            Assert.InRange(Math.Abs(window.Bounds.Width / 5 - window.FindControl<Border>("InspectorPanel")!.Bounds.Width), 0, 1);
            var settings = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsButton"); settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<SettingsWindow>(Assert.Single(window.OwnedWindows));
            var theme = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeChoice");
            Assert.Empty(dialog.GetVisualDescendants().OfType<Slider>()); Assert.False(dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "StartWithWindows").IsChecked);
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "SaveSettings"); theme.SelectedItem = "Light";
            var screenshots = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS");
            if (screenshots != null) { Directory.CreateDirectory(screenshots); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = dialog.CaptureRenderedFrame(); frame!.Save(Path.Combine(screenshots, "settings.png")); }
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal("Light", new AppearanceStore(root).Load().Theme);
        }
        finally { window.Close(); }
        var reopened = new MainWindow(new MainViewModel(new WorkspaceStore(root), seedSampleData: true)); reopened.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); Assert.Equal(ThemeVariant.Light, reopened.ActualThemeVariant);
        }
        finally { reopened.Close(); }
    }
    [AvaloniaFact] public void CancellingSettingsPreservesAppearanceAndData()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows); dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeChoice").SelectedItem = "Light";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant); Assert.False(File.Exists(new AppearanceStore(root).FilePath)); Assert.Equal(2, vm.Data.Projects.Count);
        }
        finally { window.Close(); }
    }
}
