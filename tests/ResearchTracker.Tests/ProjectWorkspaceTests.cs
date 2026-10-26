using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ResearchTracker.Application;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class ProjectWorkspaceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerProjects-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [AvaloniaFact] public void DeleteProjectCleansReferencesPersistsAndCanBeUndoneFromCanvas()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); var project = vm.SelectedProject!; var taskIds = vm.Data.Tasks.Where(t => t.ProjectId == project.Id).Select(t => t.Id).ToHashSet();
            var other = vm.Data.Tasks.First(t => t.ProjectId != project.Id); other.Dependencies.Add(taskIds.First()); vm.Data.Days[0].AllowedProjects.Add(project.Id); vm.Save();
            Assert.Null(window.FindControl<TextBox>("ProjectName"));
            window.FindControl<Button>("DeleteProjectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(vm.Data.Projects, p => p.Id == project.Id); Assert.DoesNotContain(vm.Data.Nodes, n => n.ProjectId == project.Id); Assert.DoesNotContain(vm.Data.Edges, e => e.ProjectId == project.Id); Assert.DoesNotContain(vm.Data.Tasks, t => t.ProjectId == project.Id);
            Assert.DoesNotContain(vm.Data.Tasks.SelectMany(t => t.Dependencies), id => taskIds.Contains(id)); Assert.DoesNotContain(vm.Data.Days.SelectMany(d => d.AllowedProjects), id => id == project.Id); WorkspaceStore.Validate(vm.Store.Load()!);
            window.FindControl<Button>("UndoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains(vm.Data.Projects, p => p.Id == project.Id); Assert.Contains(vm.Data.Tasks, t => taskIds.Contains(t.Id)); WorkspaceStore.Validate(vm.Store.Load()!);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void NewProjectPresetsAndCustomPickerAreAvailableAndPersistSelectedColor()
    {
        var dialog = new NewProjectWindow(GraphColors.ProjectPalette[0]); dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); var presets = dialog.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "NewProjectColorPresets"); Assert.Equal(GraphColors.ProjectPalette.Length, presets.Children.Count);
            var swatch = (Button)presets.Children[2]; swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(new Thickness(2), swatch.BorderThickness);
            dialog.GetVisualDescendants().OfType<Button>().Single(p => p.Name == "NewProjectColor").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); Assert.IsType<ProjectColorWindow>(Assert.Single(dialog.OwnedWindows)); foreach (var child in dialog.OwnedWindows.ToArray()) child.Close();
        }
        finally { dialog.Close(); }
    }
    [AvaloniaFact] public void ProjectMenuDeletesItsOwnProjectAndNavigationAndWeekFitAtNarrowWindow()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); var project = vm.Data.Projects[1];
            var options = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "⋯" && b.DataContext == project);
            var flyout = Assert.IsType<MenuFlyout>(options.Flyout); flyout.ShowAt(options); Dispatcher.UIThread.RunJobs(); var delete = Assert.IsType<MenuItem>(Assert.Single(flyout.Items)); Assert.Same(project, delete.DataContext);
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); flyout.Hide(); Assert.DoesNotContain(vm.Data.Projects, p => p.Id == project.Id);
            window.Width = 1100; window.Height = 720; var tabs = window.FindControl<TabControl>("MainTabs")!; Dispatcher.UIThread.RunJobs();
            var headings = tabs.Items.OfType<TabItem>().ToArray(); var original = headings.Select(t => t.TranslatePoint(default, window)!.Value).ToArray();
            var sidebarTop = window.FindControl<Border>("ProjectSidebar")!.TranslatePoint(default, window)!.Value.Y;
            Assert.All(headings, t => Assert.True(t.TranslatePoint(default, window)!.Value.Y + t.Bounds.Height <= sidebarTop + 1));
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Assert.Equal(original, headings.Select(t => t.TranslatePoint(default, window)!.Value).ToArray());
            tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); Assert.Equal(original, headings.Select(t => t.TranslatePoint(default, window)!.Value).ToArray());
            Assert.Equal(2, tabs.SelectedIndex); Assert.Null(window.FindControl<Button>("PlanNavigation")); Assert.Equal(new[] { "Projects", "Tasks", "Plan" }, tabs.Items.OfType<TabItem>().Select(t => (string)t.Header!));
            var scroll = window.FindControl<ScrollViewer>("WeekScroll")!; var days = window.FindControl<Grid>("DayColumns")!; Assert.Equal(7, days.Children.Count); Assert.True(days.Bounds.Width <= scroll.Viewport.Width + 1, $"Days {days.Bounds.Width}, viewport {scroll.Viewport.Width}"); Assert.All(vm.Days, d => Assert.Equal(3, d.MaxTasks));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void ExistingWorkspaceIncludingDemoProjectsIsPreserved()
    {
        var store = new WorkspaceStore(root); var data = SampleData.Create(); var project = new Project { Title = "My project", Summary = "Real work", ColorHex = "#8972AC" }; data.Projects.Add(project);
        var task = new WorkTask { ProjectId = project.Id, Title = "Keep me", Dependencies = [data.Tasks[0].Id] }; data.Tasks.Add(task); data.Days.Add(new DayRule { Date = DateTime.Today, AllowedProjects = [data.Projects[0].Id, project.Id] }); store.Save(data);
        var vm = new MainViewModel(store); var window = new MainWindow(vm); window.Show();
        try { Dispatcher.UIThread.RunJobs(); Assert.Equal(3, vm.Data.Projects.Count); Assert.Equal(data.Nodes.Count, vm.Data.Nodes.Count); Assert.Equal(task.Dependencies, vm.Data.Tasks.Single(t => t.Id == task.Id).Dependencies); }
        finally { window.Close(); }
        Assert.Equal(3, store.Load()!.Projects.Count); Assert.Empty(Directory.GetFiles(root, "before-placeholder-cleanup-*.zip"));
    }
    [Fact] public void ExplicitCliSamplesRemainAvailable()
    {
        new WorkspaceCommands(root).workspace_init(true); var store = new WorkspaceStore(root); Assert.Equal(2, store.Load()!.Projects.Count);
    }
    [Fact] public void EveryProjectHeaderShadeHasStrongTextContrastEvenAtColorExtremes()
    {
        static double Luminance(Color color)
        {
            static double C(byte component) { var value = component / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        foreach (var color in GraphColors.ProjectPalette.Concat(["#000000", "#FFFFFF", "#FF0000"]))
        {
            var background = ProjectColors.Heading(new Project { ColorHex = color }); var foreground = ((ISolidColorBrush)ProjectColors.HeadingText).Color;
            Assert.True((Luminance(background) + .05) / (Luminance(foreground) + .05) >= 7, color);
        }
    }
    [AvaloniaFact] public void NewDesktopWorkspaceStartsEmpty()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); var window = new MainWindow(vm); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); Assert.Empty(vm.Projects); Assert.Empty(vm.Data.Nodes); Assert.Empty(vm.Data.Tasks); Assert.False(vm.HasProject); Assert.True(window.FindControl<TextBlock>("EmptyProjectHint")!.IsVisible);
            var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS"); if (output != null) { Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "empty-workspace.png")); }
        }
        finally { window.Close(); }
    }
}
