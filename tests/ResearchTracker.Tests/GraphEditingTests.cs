using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia.Connections;
using ResearchTracker.Application;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class GraphEditingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerEditing-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs(); }
    private static GraphCanvas Graph(MainWindow window)
    {
        window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); var graph = window.FindControl<GraphCanvas>("GraphView")!; graph.Fit(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return graph;
    }
    private static Point At(GraphCanvas graph, Window window, double x, double y) => graph.TranslatePoint(graph.GraphToScreen(new Point(x, y)), window)!.Value;
    private static void Screenshot(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS"); if (output == null) return;
        Directory.CreateDirectory(output); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name + ".png"));
    }
    [Fact] public void ProjectColorsRoundTripThroughCliDatabaseAndBackupAndRejectInvalidInput()
    {
        var commands = new WorkspaceCommands(root); commands.workspace_init(true); var store = new WorkspaceStore(root); var data = store.Load()!; var project = data.Projects[0];
        commands.entities_update(project.Id, color: "#123aBc"); data = store.Load()!; Assert.Equal("#123ABC", data.Projects[0].ColorHex);
        var backup = Path.Combine(root, "colors.zip"); store.Export(data, backup); var imported = new WorkspaceStore(Path.Combine(root, "restore")).Import(backup); Assert.Equal("#123ABC", imported.Projects[0].ColorHex);
        Assert.Throws<ArgumentException>(() => commands.entities_update(project.Id, color: "red")); Assert.Equal("#123ABC", store.Load()!.Projects[0].ColorHex);
        Assert.Throws<ArgumentException>(() => commands.entities_update(data.Nodes[0].Id, color: "#123456"));
        commands.entities_update(project.Id, color: ""); Assert.Null(store.Load()!.Projects[0].ColorHex);
    }
    [Fact] public void HistoryPreservesAtomicGraphTasksAndDropsRedoAfterANewEdit()
    {
        var data = SampleData.Create(); var history = new WorkspaceHistory(data); var node = data.Nodes[1]; var originalEdges = data.Edges.Count; var originalTasks = data.Tasks.Count;
        new GraphService(data).DeleteNode(node); history.Record(data); var undo = history.Undo(data)!;
        Assert.Equal(originalEdges, undo.Edges.Count); Assert.Equal(originalTasks, undo.Tasks.Count); WorkspaceStore.Validate(undo);
        var redo = history.Redo(undo)!; Assert.DoesNotContain(redo.Nodes, n => n.Id == node.Id);
        undo = history.Undo(redo)!; undo.Nodes[0].Title = "New edit"; history.Record(undo); Assert.Null(history.Redo(undo));
    }
    [AvaloniaFact] public void ProjectColorsUseBrightHeadingsAndCompactToolbarAndThemeOnlySettings()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); var node = vm.Data.Nodes[0]; var action = vm.Data.Edges[^1]; var project = vm.SelectedProject!;
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProjectColor4F8F81").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("#4F8F81", project.ColorHex);
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; vm.Selected = node; Dispatcher.UIThread.RunJobs();
            var heading = graph.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NodeHeading" + node.Id.ToString("N")); Assert.Equal(ProjectColors.Heading(project), ((ISolidColorBrush)heading.Background!).Color);
            Assert.Contains(heading.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == node.Title);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.Name == "GraphColorHex");
            vm.Selected = action; Dispatcher.UIThread.RunJobs(); var label = graph.GetVisualDescendants().OfType<BaseConnection>().Single(c => c.Name == "ActionBlock" + action.Id.ToString("N")); Assert.Equal(ProjectColors.Heading(project), ((ISolidColorBrush)label.TextBackground!).Color);
            var bar = window.FindControl<Border>("CommandBar")!; Assert.Equal(32, bar.Bounds.Height); Assert.Equal(window.Bounds.Width, bar.Bounds.Width); Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Workspace");
            var buttons = window.FindControl<StackPanel>("CommandBarActions")!.GetVisualDescendants().OfType<Button>().ToArray(); Assert.Equal(4, buttons.Length); Assert.True(buttons[0].TranslatePoint(default, window)!.Value.X < 20); Assert.All(buttons, b => Assert.Equal(new Thickness(0), b.BorderThickness));
            var settingsButton = window.FindControl<Button>("SettingsButton")!; Assert.True(settingsButton.TranslatePoint(default, window)!.Value.Y < 32); Assert.Equal("Settings", settingsButton.Content);
            Assert.Equal(Avalonia.Layout.Orientation.Horizontal, window.FindControl<StackPanel>("CommandBarActions")!.Orientation); Assert.Null(window.FindControl<StackPanel>("ActivityBarUtilities"));
            Screenshot(window, "project-colors-dark");
            buttons.Single(b => b.Name == "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var settings = Assert.Single(window.OwnedWindows); Assert.Empty(settings.GetVisualDescendants().OfType<TextBox>()); Assert.Empty(settings.GetVisualDescendants().OfType<Slider>());
            settings.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeChoice").SelectedItem = "Light";
            settings.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); Screenshot(window, "project-colors-light");
            AppearanceManager.Apply(new() { Theme = "Dark" });
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void OutputClickCreatesOnlyDanglingActionAndUnselectedEndDragsImmediately()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); var node = vm.Data.Nodes[0]; vm.Selected = node; var count = vm.Data.Edges.Count;
            var port = At(graph, window, node.X + 240, node.Y + GraphGeometry.NodeHeight / 2); window.MouseDown(port, MouseButton.Left); window.MouseUp(port, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            var action = Assert.IsType<GraphEdge>(vm.Selected); Assert.Equal(count + 1, vm.Data.Edges.Count); Assert.Equal(node.Id, action.SourceId); Assert.Null(action.TargetId); Assert.False(graph.Connecting);
            var ends = graph.ArrowEnds(action); vm.Selected = null;
            var start = At(graph, window, ends.Target.X, ends.Target.Y); var to = At(graph, window, 100, 600);
            window.MouseDown(start, MouseButton.Left); Assert.True(graph.EditingEndpoint); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Same(action, vm.Selected); Assert.Null(action.TargetId); Assert.Equal(100, action.TargetX!.Value, 5); Assert.Equal(600, action.TargetY!.Value, 5); Assert.Single(vm.Data.Tasks, t => t.EdgeId == action.Id);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void WheelPansAndControlWheelZoomsAroundPointerInBothModes()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window);
            foreach (var mode in new[] { GraphInteractionMode.Select, GraphInteractionMode.Move })
            {
                graph.Mode = mode; graph.ViewportZoom = 1; var pointer = new Point(300, 250); var at = graph.TranslatePoint(pointer, window)!.Value; var before = graph.ViewportLocation;
                window.MouseWheel(at, new Vector(0, -1)); Dispatcher.UIThread.RunJobs(); Assert.Equal(1, graph.ViewportZoom); Assert.True(graph.ViewportLocation.Y > before.Y);
                var anchor = graph.ViewportLocation + new Vector(pointer.X / graph.ViewportZoom, pointer.Y / graph.ViewportZoom);
                window.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Control); Dispatcher.UIThread.RunJobs(); Assert.True(graph.ViewportZoom > 1);
                var after = graph.ViewportLocation + new Vector(pointer.X / graph.ViewportZoom, pointer.Y / graph.ViewportZoom); Assert.Equal(anchor.X, after.X, 5); Assert.Equal(anchor.Y, after.Y, 5);
            }
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void AttachedActionDragReroutesItsLineAndUndoRedoRestorePosition()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); var action = vm.Data.Edges[0]; var ends = graph.ArrowEnds(action); var label = At(graph, window, (ends.Source.X + ends.Target.X) / 2, (ends.Source.Y + ends.Target.Y) / 2);
            window.MouseDown(label, MouseButton.Left); Assert.Same(action, vm.Selected);
            var to = label + new Point(50, 35); window.MouseMove(to, RawInputModifiers.LeftMouseButton); Screenshot(window, "graph-action-live-route"); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(action.LabelOffsetX > 40); Assert.True(action.LabelOffsetY > 20); Assert.Equal(ends, graph.ArrowEnds(action)); var savedX = action.LabelOffsetX;
            Press(window, Key.Z, RawInputModifiers.Control); Assert.Equal(0, vm.Data.Edges.Single(e => e.Id == action.Id).LabelOffsetX);
            Press(window, Key.Y, RawInputModifiers.Control); Assert.Equal(savedX, vm.Data.Edges.Single(e => e.Id == action.Id).LabelOffsetX);
            Assert.Equal(savedX, vm.Store.Load()!.Edges.Single(e => e.Id == action.Id).LabelOffsetX); Screenshot(window, "graph-moved-action");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void ActionDragMovesFreeEndsAndKeepsAttachedEndsAndSupportsCancelAndUndo()
    {
        foreach (var sourceFree in new[] { false, true })
        {
            var vm = new MainViewModel(new WorkspaceStore(root + sourceFree), seedSampleData: true);
            var action = vm.Graph.Connect(vm.Data.Nodes[0], null, "Move this action"); vm.Graph.MoveEndpoint(action, EdgeEnd.Target, null, 1100, 600);
            if (sourceFree) vm.Graph.MoveEndpoint(action, EdgeEnd.Source, null, 100, 500); vm.Refresh(); var taskId = vm.Data.Tasks.Single(t => t.EdgeId == action.Id).Id;
            var window = new MainWindow(vm); window.Show();
            try
            {
                var graph = Graph(window); var ends = graph.ArrowEnds(action); var label = At(graph, window, (ends.Source.X + ends.Target.X) / 2, (ends.Source.Y + ends.Target.Y) / 2); var delta = new Point(45, 35); var worldX = delta.X / graph.ViewportZoom; var worldY = delta.Y / graph.ViewportZoom;
                window.MouseDown(label, MouseButton.Left); Assert.Same(action, vm.Selected); window.MouseMove(label + delta, RawInputModifiers.LeftMouseButton); Dispatcher.UIThread.RunJobs();
                var during = graph.ArrowEnds(action); Assert.Equal(ends.Target.X + worldX, during.Target.X, 5); Assert.Equal(ends.Target.Y + worldY, during.Target.Y, 5);
                if (sourceFree) { Assert.Equal(ends.Source.X + worldX, during.Source.X, 5); Assert.Equal(ends.Source.Y + worldY, during.Source.Y, 5); } else Assert.Equal(ends.Source, during.Source);
                Press(window, Key.Escape); window.MouseUp(label + delta, MouseButton.Left); Assert.Equal(ends, graph.ArrowEnds(action));
                window.MouseDown(label, MouseButton.Left); window.MouseMove(label + delta, RawInputModifiers.LeftMouseButton); window.MouseUp(label + delta, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                Assert.Equal(taskId, vm.Data.Tasks.Single(t => t.EdgeId == action.Id).Id); Assert.Equal(ends.Target.X + worldX, graph.ArrowEnds(action).Target.X, 5); WorkspaceStore.Validate(vm.Store.Load()!);
                Press(window, Key.Z, RawInputModifiers.Control); var restored = vm.Data.Edges.Single(e => e.Id == action.Id); Assert.Equal(ends, graph.ArrowEnds(restored));
                Press(window, Key.Y, RawInputModifiers.Control); Assert.Equal(ends.Target.X + worldX, graph.ArrowEnds(vm.Data.Edges.Single(e => e.Id == action.Id)).Target.X, 5);
            }
            finally { window.Close(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root + sourceFree, true); }
        }
    }
    [AvaloniaFact] public void SelectModeBoxesGroupsDuplicatesLinksAndUndoRedoWorkFromKeyboard()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); window.FindControl<Button>("SelectModeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(GraphInteractionMode.Select, graph.Mode);
            var beforeViewport = graph.ViewportLocation; var start = At(graph, window, 40, 60); var finish = At(graph, window, 665, 306);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(finish, RawInputModifiers.LeftMouseButton); window.MouseUp(finish, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Equal(beforeViewport, graph.ViewportLocation); Assert.Contains(vm.Data.Nodes[0].Id, vm.GraphSelection); Assert.Contains(vm.Data.Nodes[1].Id, vm.GraphSelection); Assert.True(vm.GraphSelection.Count >= 2);
            var count = vm.Data.Nodes.Count; Press(window, Key.D, RawInputModifiers.Control); Assert.True(vm.Data.Nodes.Count > count);
            var newNodes = vm.Data.Nodes.Where(n => n.Title.EndsWith(" (copy)")).Select(n => n.Id).ToHashSet(); Assert.Contains(vm.Data.Edges, e => e.SourceId != null && e.TargetId != null && newNodes.Contains(e.SourceId.Value) && newNodes.Contains(e.TargetId.Value));
            Press(window, Key.Z, RawInputModifiers.Control); Assert.Equal(count, vm.Data.Nodes.Count);
            Press(window, Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift); Assert.True(vm.Data.Nodes.Count > count);
            graph.Focus(); Press(window, Key.A, RawInputModifiers.Control); Assert.Equal(vm.Nodes.Count() + vm.Edges.Count(), vm.GraphSelection.Count); Screenshot(window, "graph-select-mode");
            Press(window, Key.H); Assert.Equal(GraphInteractionMode.Move, graph.Mode);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void GroupMoveIsOneUndoAndCopyPasteRetainsColorsAndTaskMetadata()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); var first = vm.Data.Nodes[0]; var second = vm.Data.Nodes[1]; first.ColorHex = "#123456"; vm.Save(); vm.SelectGraph([first, second]);
            var originalA = first.X; var originalB = second.X; var from = At(graph, window, first.X + 60, first.Y + GraphGeometry.NodeHeight / 2); var to = from + new Point(40, 20);
            window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(first.X > originalA); Assert.Equal(first.X - originalA, second.X - originalB, 5);
            Press(window, Key.Z, RawInputModifiers.Control); Assert.Equal(originalA, vm.Data.Nodes.Single(n => n.Id == first.Id).X); Assert.Equal(originalB, vm.Data.Nodes.Single(n => n.Id == second.Id).X);
            var node = vm.Data.Nodes.Single(n => n.Id == first.Id); vm.Selected = node; var pasted = Assert.IsType<GraphNode>(vm.Paste(vm.CopySelected())); Assert.Equal("#123456", pasted.ColorHex); Assert.NotEqual(node.Id, pasted.Id);
            var task = vm.Data.Tasks.First(); vm.Selected = task; var taskCopy = Assert.IsType<WorkTask>(vm.Paste(vm.CopySelected())); Assert.Null(taskCopy.EdgeId); Assert.Equal(task.Priority, taskCopy.Priority); Assert.Equal(task.EffortHours, taskCopy.EffortHours); Assert.Null(taskCopy.ScheduledStart);
            vm.Selected = node; vm.DeleteSelected(); vm.Undo(); Assert.Contains(vm.Data.Nodes, n => n.Id == node.Id); WorkspaceStore.Validate(vm.Store.Load()!);
            var nodeCount = vm.Data.Nodes.Count; vm.Selected = vm.Data.Nodes[0]; graph.Focus(); Press(window, Key.C, RawInputModifiers.Control); Press(window, Key.V, RawInputModifiers.Control); Assert.Equal(nodeCount + 1, vm.Data.Nodes.Count);
            Press(window, Key.X, RawInputModifiers.Control); Assert.Equal(nodeCount, vm.Data.Nodes.Count); Press(window, Key.Z, RawInputModifiers.Control); Assert.Equal(nodeCount + 1, vm.Data.Nodes.Count);
            vm.Selected = vm.Data.Nodes[0]; Dispatcher.UIThread.RunJobs();
            var title = window.FindControl<StackPanel>("Inspector")!.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == vm.Selected.Title); title.Focus(); title.CaretIndex = title.Text!.Length; window.KeyTextInput("z"); Press(window, Key.Z, RawInputModifiers.Control); Assert.False(title.Text!.EndsWith("z")); Assert.Equal(nodeCount + 1, vm.Data.Nodes.Count);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void ScrollbarsNavigateSelectModeAndCalendarMarksTodayAndPastDates()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var graph = Graph(window); graph.Mode = GraphInteractionMode.Select; graph.ViewportZoom = 2; Dispatcher.UIThread.RunJobs();
            var horizontal = window.FindControl<ScrollBar>("GraphHorizontalScroll")!; var vertical = window.FindControl<ScrollBar>("GraphVerticalScroll")!;
            Assert.True(horizontal.IsVisible); Assert.True(vertical.IsVisible); var before = graph.ViewportLocation;
            var bounds = horizontal.TranslatePoint(default, window)!.Value; var click = bounds + new Point(horizontal.Bounds.Width - 8, horizontal.Bounds.Height / 2);
            window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(graph.ViewportLocation.X > before.X); Assert.Equal(GraphInteractionMode.Select, graph.Mode); Screenshot(window, "graph-scrollbars");
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; vm.MonthView = true; vm.ShowCalendarDate(DateTime.Today); Dispatcher.UIThread.RunJobs();
            var view = Assert.IsType<MonthCalendarView>(window.FindControl<ContentControl>("MonthHost")!.Content);
            var today = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MonthDate" + DateTime.Today.ToString("yyyyMMdd"));
            Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Accent")).Color, ((ISolidColorBrush)today.Background!).Color);
            var yesterday = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MonthDate" + DateTime.Today.AddDays(-1).ToString("yyyyMMdd")); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("PastText")).Color, ((ISolidColorBrush)yesterday.Foreground!).Color);
            var previous = window.FindControl<Button>("CalendarPrevious")!; var next = window.FindControl<Button>("CalendarNext")!;
            Assert.True(previous.TranslatePoint(default, window)!.Value.X < view.TranslatePoint(default, window)!.Value.X); Assert.True(next.TranslatePoint(default, window)!.Value.X > view.TranslatePoint(default, window)!.Value.X);
            var date = vm.CalendarDate; next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(CalendarDates.MonthStart(date).AddMonths(1), vm.CalendarDate); previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); vm.ShowCalendarDate(DateTime.Today);
            Screenshot(window, "calendar-today");
            window.FindControl<Button>("WeekViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var weekToday = window.FindControl<Grid>("DayColumns")!.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WeekDate" + DateTime.Today.ToString("yyyyMMdd")); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Accent")).Color, ((ISolidColorBrush)weekToday.Background!).Color);
        }
        finally { window.Close(); }
    }
}
