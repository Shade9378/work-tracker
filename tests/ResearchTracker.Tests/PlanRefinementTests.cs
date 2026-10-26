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
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class PlanRefinementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerPlan-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    private static void Screenshot(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS"); if (output == null) return; Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, name + ".png"));
    }
    [AvaloniaFact] public void TaskTrashDeletesStandaloneAndLinkedActionsAndUndoRestoresThem()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            foreach (var id in vm.Data.Tasks.Where(t => t.EdgeId == null).Take(1).Concat(vm.Data.Tasks.Where(t => t.EdgeId != null)).Select(t => t.Id).ToArray())
            {
                var task = vm.Data.Tasks.Single(t => t.Id == id); var edgeId = task.EdgeId;
                var trash = window.FindControl<StackPanel>("TaskList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteTask" + id.ToString("N"));
                Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Danger")).Color, ((ISolidColorBrush)trash.Foreground!).Color);
                var at = trash.TranslatePoint(new Point(trash.Bounds.Width / 2, trash.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left); Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(vm.Data.Tasks, t => t.Id == id); if (edgeId != null) Assert.DoesNotContain(vm.Data.Edges, e => e.Id == edgeId);
                WorkspaceStore.Validate(vm.Store.Load()!); vm.Undo(); Dispatcher.UIThread.RunJobs(); Assert.Contains(vm.Data.Tasks, t => t.Id == id); if (edgeId != null) Assert.Contains(vm.Data.Edges, e => e.Id == edgeId);
            }
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void ProjectlessTaskCanBeCreatedEditedScheduledCompletedAndRestored()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HideCompleted); Assert.All(vm.Days, d => Assert.False(d.Unavailable)); Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Hide finished")).IsChecked);
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Add standalone task")));
            var task = Assert.Single(vm.Data.Tasks); Assert.Null(task.ProjectId); Assert.Empty(vm.Data.Projects);
            var editor = Assert.Single(window.OwnedWindows); var picker = editor.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TaskProjectPicker"); Assert.Equal("No project", picker.SelectedItem); editor.Close(); Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.Store.Load()!.Tasks.Single().ProjectId); vm.Selected = task; var pasted = Assert.IsType<WorkTask>(vm.Paste(vm.CopySelected())); Assert.Null(pasted.ProjectId);
            var day = vm.Days.First(); day.Unavailable = false; var when = day.Date.AddHours(9); vm.ManualSchedule(task, when); Assert.Empty(vm.Scheduler.Conflicts(vm.Data, task, when));
            var done = window.FindControl<StackPanel>("TaskList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CompleteTask" + task.Id.ToString("N")); Click(done);
            Assert.Equal(WorkStatus.Done, task.Status); Assert.NotNull(task.CompletedAt); Assert.DoesNotContain(vm.Tasks, t => t.Id == task.Id); Assert.Equal(task.CompletedAt, vm.Store.Load()!.Tasks.Single(t => t.Id == task.Id).CompletedAt); Assert.Equal(WorkStatus.Done, vm.Store.Load()!.Tasks.Single(t => t.Id == task.Id).Status);
            vm.Undo(); Assert.Equal(WorkStatus.Todo, vm.Data.Tasks.Single(t => t.Id == task.Id).Status); WorkspaceStore.Validate(vm.Store.Load()!);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void CompleteLinkedTaskIconRecordsOutcomeWithoutLosingGraphSynchronization()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); var task = vm.Data.Tasks.First(t => t.EdgeId != null);
            Click(window.FindControl<StackPanel>("TaskList")!.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CompleteTask" + task.Id.ToString("N")));
            var dialog = Assert.Single(window.OwnedWindows); dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CompleteOutcomeTitle").Text = "Measured outcome";
            Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmTaskComplete")); Assert.Empty(window.OwnedWindows); Assert.Equal(WorkStatus.Done, task.Status);
            var edge = vm.Data.Edges.Single(e => e.Id == task.EdgeId); Assert.NotNull(edge.TargetId); Assert.Contains(vm.Data.Nodes, n => n.Id == edge.TargetId && n.Title == "Measured outcome"); WorkspaceStore.Validate(vm.Store.Load()!);
            vm.Undo(); Assert.Null(vm.Data.Edges.Single(e => e.Id == edge.Id).TargetId);
        }
        finally { window.Close(); }
    }
    [Fact] public void TaskPlacementHasOneStatusWithConflictReasonAndRecordedCompletionTime()
    {
        var task = new WorkTask { ScheduledStart = new DateTime(2030, 1, 7, 10, 0, 0) };
        Assert.StartsWith("SCHEDULED - ", TaskSchedule.Placement(task, []));
        var conflict = TaskSchedule.Placement(task, ["Day is unavailable."]); Assert.Equal("CONFLICT - Day is unavailable.", conflict); Assert.DoesNotContain("SCHEDULED", conflict);
        var data = new Workspace { Tasks = [task] }; var graph = new GraphService(data); graph.Complete(task, ""); var completed = task.CompletedAt; Assert.NotNull(completed);
        Assert.StartsWith("DONE - ", TaskSchedule.Placement(task, [])); Assert.Contains(completed.Value.LocalDateTime.ToString("HH:mm - MMM d, yyyy"), TaskSchedule.Placement(task, [])); graph.Synchronize(); Assert.Equal(completed, task.CompletedAt);
        task.Status = WorkStatus.Todo; graph.UpdateTask(task); Assert.Null(task.CompletedAt);
    }
    [Fact] public void DefaultIsThreeTasksAndScheduleDatesExcludeMidnightEnd()
    {
        Assert.Equal(3, new DayRule().MaxTasks);
        var task = new WorkTask { ScheduledStart = new DateTime(2030, 1, 7, 12, 0, 0), EffortHours = 36 };
        Assert.Equal(new[] { new DateTime(2030, 1, 7), new DateTime(2030, 1, 8) }, TaskSchedule.Days(task)); Assert.Contains("2 days", TaskSchedule.Description(task)); Assert.Contains("Mon, Jan 7", TaskSchedule.Description(task)); Assert.Contains("Tue, Jan 8", TaskSchedule.Description(task));
        task.ScheduledStart = null; Assert.Empty(TaskSchedule.Days(task)); Assert.Equal("Unscheduled", TaskSchedule.Description(task));
    }
    [AvaloniaFact] public void ProjectCreationAndRgbPickerSaveWithoutRequiringSidebarText()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); var window = new MainWindow(vm); window.Show();
        try
        {
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Add project")));
            var create = Assert.IsType<NewProjectWindow>(Assert.Single(window.OwnedWindows)); var title = create.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NewProjectTitle"); title.Text = "A real project";
            Click(create.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CreateProject")); Assert.Equal("A real project", Assert.Single(vm.Projects).Title); Assert.Same(vm.Projects[0], vm.SelectedProject); Assert.True(vm.HasProject);
            Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "More colors…")));
            var picker = Assert.IsType<ProjectColorWindow>(Assert.Single(window.OwnedWindows)); var board = picker.GetVisualDescendants().OfType<ColorView>().Single(); Assert.True(board.IsColorSpectrumVisible); Assert.True(board.IsComponentSliderVisible); Assert.True(board.IsComponentTextInputVisible); board.Color = Color.Parse("#9933CC"); Dispatcher.UIThread.RunJobs();
            Assert.Contains(picker.GetVisualDescendants(), control => control is ColorSpectrum); Screenshot(picker, "project-custom-rgb");
            Click(picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveProjectColor")); Assert.Equal("#9933CC", vm.SelectedProject!.ColorHex); Assert.Equal("#9933CC", vm.Store.Load()!.Projects[0].ColorHex);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void PlanHidesSidePanelsShowsStatusesAndResizableBacklogAndKeepsTaskEditing()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), true); var window = new MainWindow(vm); window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("MainTabs")!; tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.Equal("Plan", ((TabItem)tabs.Items[2]!).Header); Assert.False(window.FindControl<Border>("ProjectSidebar")!.IsVisible); Assert.False(window.FindControl<Border>("InspectorPanel")!.IsVisible);
            Assert.True(window.FindControl<Border>("BacklogPanel")!.Bounds.Height >= 250); Assert.NotNull(window.FindControl<GridSplitter>("BacklogSplitter"));
            var pane = window.FindControl<Border>("BacklogPanel")!; var oldHeight = pane.Bounds.Height; var grip = window.FindControl<GridSplitter>("BacklogSplitter")!; var point = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Point(0, -55), RawInputModifiers.LeftMouseButton); window.MouseUp(point + new Point(0, -55), MouseButton.Left); Dispatcher.UIThread.RunJobs(); Assert.True(pane.Bounds.Height > oldHeight + 30);
            Assert.DoesNotContain(vm.Issues, i => i.Message.Contains("run Auto-schedule")); Assert.DoesNotContain("Unscheduled", window.FindControl<TextBlock>("ConflictSummary")!.Text ?? "");
            var task = vm.Data.Tasks.First(t => t.EdgeId == null); task.Deadline = null; task.EffortHours = 1; var date = DateTime.Today.AddDays(8); while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1); vm.ManualSchedule(task, date.AddHours(10)); vm.ShowCalendarDate(date); Dispatcher.UIThread.RunJobs();
            var scheduled = window.GetVisualDescendants().OfType<ScheduledTaskCard>().Single(c => c.Name == "ScheduledTask" + task.Id.ToString("N")); Assert.Contains(scheduled.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == task.Title); Assert.DoesNotContain(scheduled.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("SCHEDULED") == true);
            var unscheduled = window.FindControl<StackPanel>("Backlog")!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Unscheduled"); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Danger")).Color, ((ISolidColorBrush)unscheduled.Foreground!).Color);
            Screenshot(window, "plan-full-width");
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); Assert.False(window.FindControl<Border>("ProjectSidebar")!.IsVisible); Assert.False(window.FindControl<Border>("InspectorPanel")!.IsVisible);
            Click(window.FindControl<StackPanel>("TaskList")!.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Edit task"))); var editor = Assert.Single(window.OwnedWindows); Assert.Equal("Task details", editor.Title); var description = editor.GetVisualDescendants().OfType<TextBox>().First(t => t.AcceptsReturn); Assert.Equal(Avalonia.Layout.VerticalAlignment.Top, description.VerticalContentAlignment); Assert.True(description.MinHeight < 72); editor.Close(); Dispatcher.UIThread.RunJobs();
            tabs.SelectedIndex = 0; Dispatcher.UIThread.RunJobs(); Assert.True(window.FindControl<Border>("ProjectSidebar")!.IsVisible); Assert.True(window.FindControl<Border>("InspectorPanel")!.IsVisible);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void TodayHasMatchingWeekBorderAndPastConstraintsAreDisabled()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; vm.ShowCalendarDate(DateTime.Today); Dispatcher.UIThread.RunJobs(); var days = window.FindControl<Grid>("DayColumns")!;
            var today = days.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WeekDay" + DateTime.Today.ToString("yyyyMMdd")); Assert.Equal(new Thickness(2), today.BorderThickness); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Accent")).Color, ((ISolidColorBrush)today.BorderBrush!).Color);
            vm.ChangeWeek(-1); Dispatcher.UIThread.RunJobs(); Assert.All(days.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "Constraints")), button => Assert.False(button.IsEnabled));
            vm.MonthView = true; vm.ShowCalendarDate(DateTime.Today); Dispatcher.UIThread.RunJobs(); Screenshot(window, "plan-muted-past");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void ResizePreservesGraphViewportAndMultiSelectInspectorIsEmptyAndSelectedEdgesStayHighlighted()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); var graph = window.FindControl<GraphCanvas>("GraphView")!; graph.ViewportZoom = 1.5; graph.ViewportLocation = new Point(125, 210); var location = graph.ViewportLocation;
            window.Width = 1200; window.Height = 760; Dispatcher.UIThread.RunJobs(); Assert.Equal(1.5, graph.ViewportZoom); Assert.Equal(location, graph.ViewportLocation);
            vm.SelectGraph(vm.Data.Nodes.Take(2)); Dispatcher.UIThread.RunJobs(); Assert.Empty(window.FindControl<StackPanel>("Inspector")!.Children);
            var edge = vm.Data.Edges[0]; vm.Selected = edge; vm.Refresh(); Dispatcher.UIThread.RunJobs();
            var connection = graph.GetVisualDescendants().OfType<BaseConnection>().Single(c => c.Name == "ActionBlock" + edge.Id.ToString("N")); Assert.Equal(3, connection.StrokeThickness); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Accent")).Color, ((ISolidColorBrush)connection.Stroke!).Color);
            graph.Focus(); window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null); window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs(); Assert.DoesNotContain(vm.Data.Edges, e => e.Id == edge.Id); Assert.Empty(window.OwnedWindows); vm.Undo(); Assert.Contains(vm.Data.Edges, e => e.Id == edge.Id);
        }
        finally { window.Close(); }
    }
}
