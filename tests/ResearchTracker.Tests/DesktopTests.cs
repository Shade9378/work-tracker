using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Threading;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ResearchTracker.Tests.TestApplication))]
namespace ResearchTracker.Tests;
public class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont();
}
public class DesktopTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "WorkTrackerUiTests-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string root) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    [AvaloniaFact] public void WindowRendersEveryTabAndRichInspector()
    {
        var root = Root(); var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.FindControl<GraphCanvas>("GraphView")); Assert.Equal(2, window.FindControl<ListBox>("ProjectList")!.ItemCount);
            vm.Selected = vm.Data.Nodes.First(n => n.Content.Count > 0); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<StackPanel>("Inspector")!.Children.Count > 8);
            var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS");
            void Screenshot(string name)
            {
                if (output == null) return; Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(output, name + ".png"));
            }
            var projectTabs = window.FindControl<TabControl>("ProjectTabs")!; projectTabs.SelectedIndex = 0; Dispatcher.UIThread.RunJobs(); Screenshot("project-overview"); projectTabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var graph = window.FindControl<GraphCanvas>("GraphView")!;
            Assert.True(graph.Bounds.Width > 700, "Graph width: " + graph.Bounds.Width);
            Assert.InRange(graph.GraphToScreen(new Point(240, 0)).X - graph.GraphToScreen(default).X, 120, 350);
            Screenshot("graph");
            vm.Selected = vm.Data.Edges.Last(); Dispatcher.UIThread.RunJobs(); Screenshot("arrow-handles");
            var tabs = window.FindControl<TabControl>("MainTabs")!; tabs.SelectedIndex = 1; vm.Selected = vm.Tasks.First(); Dispatcher.UIThread.RunJobs(); Screenshot("tasks");
            Assert.Equal(3, window.FindControl<StackPanel>("TaskList")!.Children.Count);
            tabs.SelectedIndex = 2; vm.ChangeWeek(1); vm.AutoSchedule(); Dispatcher.UIThread.RunJobs(); Screenshot("planner");
            Assert.Equal(7, window.FindControl<Grid>("DayColumns")!.Children.Count);
            var constraints = window.FindControl<Grid>("DayColumns")!.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Constraints"));
            constraints.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows); Assert.Contains("Constraints", dialog.Title); Assert.Equal(4, dialog.GetVisualDescendants().OfType<NumericUpDown>().Count());
            if (output != null) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = dialog.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "day-constraints.png")); }
            dialog.Close(); Dispatcher.UIThread.RunJobs();
            var settings = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsButton");
            void ChangeTheme(string name)
            {
                settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                var preferences = Assert.Single(window.OwnedWindows);
                preferences.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeChoice").SelectedItem = name;
                preferences.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            }
            ChangeTheme("Light");
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant); Screenshot("planner-light");
            tabs.SelectedIndex = 0; Dispatcher.UIThread.RunJobs(); Screenshot("graph-light");
            ChangeTheme("Dark"); Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.DoesNotContain("FAILED", vm.Status);
        }
        finally { window.Close(); Cleanup(root); }
    }
    [AvaloniaFact] public void CanvasSupportsSelectionDraggingAndDanglingConnections()
    {
        var root = Root(); var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); var canvas = window.FindControl<GraphCanvas>("GraphView")!; canvas.Fit();
            Point At(double x, double y) => canvas.TranslatePoint(canvas.GraphToScreen(new Point(x, y)), window)!.Value;
            var originalEdge = vm.Data.Edges[0]; var edgeSource = vm.Data.Nodes.Single(n => n.Id == originalEdge.SourceId); var edgeTarget = vm.Data.Nodes.Single(n => n.Id == originalEdge.TargetId);
            var label = At((edgeSource.X + 240 + edgeTarget.X) / 2, (edgeSource.Y + edgeTarget.Y) / 2 + GraphGeometry.NodeHeight / 2);
            window.MouseDown(label, MouseButton.Left); window.MouseUp(label, MouseButton.Left); Assert.Same(originalEdge, vm.Selected);
            var source = vm.Data.Nodes[0]; var oldX = source.X;
            var start = At(source.X + 60, source.Y + GraphGeometry.NodeHeight / 2);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Point(8, 4), RawInputModifiers.LeftMouseButton); window.MouseMove(start + new Point(45, 20), RawInputModifiers.LeftMouseButton); window.MouseUp(start + new Point(45, 20), MouseButton.Left);
            Assert.Same(source, vm.Selected); Assert.True(source.X > oldX, $"Node X={source.X}, old={oldX}, dragging={canvas.IsDragging}, selected={canvas.SelectedItems?.Count}, viewport={canvas.ViewportLocation}");
            var port = At(source.X + 240, source.Y + GraphGeometry.NodeHeight / 2); var edgeCount = vm.Data.Edges.Count;
            window.MouseDown(port, MouseButton.Left); window.MouseUp(port, MouseButton.Left);
            Assert.Equal(edgeCount + 1, vm.Data.Edges.Count); var action = Assert.IsType<GraphEdge>(vm.Selected); Assert.Null(action.TargetId); Assert.Contains(vm.Data.Tasks, t => t.EdgeId == action.Id);
        }
        finally { window.Close(); Cleanup(root); }
    }
    [AvaloniaFact] public void ProjectSelectionAndTaskEditsSurviveRefreshAndReload()
    {
        var root = Root(); var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var selected = vm.SelectedProject; vm.Refresh(); Assert.Same(selected, vm.SelectedProject);
            var node = vm.AddNode("New state"); Assert.Same(node, vm.Selected); Assert.Same(selected, vm.SelectedProject);
            var edge = vm.Graph.Connect(node, null, "New action"); vm.Refresh(); var task = vm.Data.Tasks.Single(t => t.EdgeId == edge.Id);
            vm.Selected = task; task.Title = "Changed from inspector"; Assert.Equal(task.Title, edge.Title);
            vm.Save(); Assert.Equal(task.Title, vm.Store.Load()!.Edges.Single(e => e.Id == edge.Id).Title);
            vm.SelectedProject = vm.Data.Projects[1]; vm.Refresh(); Assert.Equal(vm.Data.Projects[1].Id, vm.SelectedProject!.Id); Assert.Equal(4, vm.Tasks.Count());
        }
        finally { window.Close(); Cleanup(root); }
    }
    [AvaloniaFact] public void EmptyWorkspaceCanCreateFirstProjectAndOutcome()
    {
        var root = Root(); var store = new WorkspaceStore(root); store.Save(new Workspace()); var vm = new MainViewModel(store); var window = new MainWindow(vm); window.Show();
        try
        {
            var p = new Project { Title = "First" }; vm.Data.Projects.Add(p); vm.SelectedProject = p; vm.Refresh();
            var n = vm.AddNode(); var e = vm.Graph.Connect(n, null, "First action"); vm.Refresh();
            var result = vm.Graph.Complete(vm.Data.Tasks.Single(t => t.EdgeId == e.Id), "First result"); vm.Refresh(); vm.Selected = result;
            Assert.Equal(2, store.Load()!.Nodes.Count); Assert.Equal(WorkStatus.Done, store.Load()!.Tasks[0].Status);
        }
        finally { window.Close(); Cleanup(root); }
    }
}
