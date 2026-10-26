using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class CalendarInteractionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerCalendarInteraction-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static void Click(Window window, Control control, Point? local = null)
    {
        var point = control.TranslatePoint(local ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    [Fact] public void TimeRulerSnapsToQuarterHoursAndClampsToTheChosenDay()
    {
        var day = new DateTime(2030, 1, 7);
        Assert.Equal(day.AddHours(9).AddMinutes(15), WeekTimeScale.TimeAt(day, 9.2 * WeekTimeScale.PixelsPerHour));
        Assert.Equal(day, WeekTimeScale.TimeAt(day, -100)); Assert.Equal(day.AddHours(23).AddMinutes(45), WeekTimeScale.TimeAt(day, 30 * WeekTimeScale.PixelsPerHour));
        Assert.Equal(10.5 * WeekTimeScale.PixelsPerHour, WeekTimeScale.Position(day.AddHours(10.5)));
    }
    [AvaloniaFact] public void WeeklyCardsOpenTheEditorAndRemoveOnlyTheirCalendarReservation()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), true); var window = new MainWindow(vm); window.Show();
        try
        {
            var task = vm.Data.Tasks.First(t => t.EdgeId == null); task.Title = "Concise calendar task"; vm.ShowCalendarDate(new DateTime(2030, 1, 7)); vm.ManualSchedule(task, vm.WeekStart.AddHours(9)); window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            var card = window.GetVisualDescendants().OfType<ScheduledTaskCard>().Single(c => c.Name == "ScheduledTask" + task.Id.ToString("N"));
            Assert.Equal(new[] { task.Title, vm.Data.Projects.Single(p => p.Id == task.ProjectId).Title }, card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text != "×").Select(t => t.Text).ToArray());
            Click(window, card, new Point(8, 8)); var editor = Assert.Single(window.OwnedWindows); Assert.Equal("Task details", editor.Title); editor.Close(); Dispatcher.UIThread.RunJobs();
            card = window.GetVisualDescendants().OfType<ScheduledTaskCard>().Single(c => c.Name == "ScheduledTask" + task.Id.ToString("N")); var remove = card.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "UnscheduleTask" + task.Id.ToString("N")); Click(window, remove);
            Assert.Null(task.ScheduledStart); Assert.False(task.Manual); Assert.Contains(vm.Data.Tasks, t => t.Id == task.Id); Assert.Empty(window.OwnedWindows); Assert.Null(vm.Store.Load()!.Tasks.Single(t => t.Id == task.Id).ScheduledStart);
            vm.Undo(); Assert.Equal(new DateTime(2030, 1, 7, 9, 0, 0), vm.Data.Tasks.Single(t => t.Id == task.Id).ScheduledStart);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void WeekResizingAndHoverKeepSevenStableColumnsAndIndependentScrollPositions()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs(); var start = vm.WeekStart;
            foreach (var width in new[] { 1400, 1100, 1250, 1100 })
            {
                window.Width = width; window.Height = 760; Dispatcher.UIThread.RunJobs(); var days = window.FindControl<Grid>("DayColumns")!; Assert.Equal(7, days.Children.Count);
                var scroll = window.FindControl<ScrollViewer>("WeekScroll")!; Assert.True(days.Bounds.Width <= scroll.Viewport.Width + 1);
                foreach (var child in days.Children) { var at = child.TranslatePoint(new Point(child.Bounds.Width - 2, 20), window)!.Value; window.MouseMove(at); Dispatcher.UIThread.RunJobs(); Assert.InRange(child.Bounds.Width, days.Bounds.Width / 7 - 5, days.Bounds.Width / 7 + 1); }
                Assert.Equal(start, vm.WeekStart);
            }
            var timelines = window.GetVisualDescendants().OfType<WeekDayTimeline>().OrderBy(t => t.Day).ToArray(); Assert.Equal(7, timelines.Length); timelines[0].Offset = new Vector(0, 12 * WeekTimeScale.PixelsPerHour); var other = timelines[1].Offset;
            Dispatcher.UIThread.RunJobs(); Assert.Equal(other, timelines[1].Offset); vm.Refresh(); Dispatcher.UIThread.RunJobs(); var restored = window.GetVisualDescendants().OfType<WeekDayTimeline>().Single(t => t.Day == start); Assert.True(Math.Abs(restored.Offset.Y - 12 * WeekTimeScale.PixelsPerHour) < 1, $"Old offset {timelines[0].Offset}, old extent {timelines[0].Extent}, old viewport {timelines[0].Viewport}, new offset {restored.Offset}, new extent {restored.Extent}, new viewport {restored.Viewport}");
            var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS"); if (output != null) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "week-timeline-narrow.png")); }
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void MonthUsesCompactCardsWithoutHourlyRulers()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), true); var window = new MainWindow(vm); window.Show();
        try
        {
            var task = vm.Data.Tasks.First(); vm.ManualSchedule(task, new DateTime(2030, 1, 7, 9, 0, 0)); vm.MonthView = true; vm.ShowCalendarDate(new DateTime(2030, 1, 7)); window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            var month = window.FindControl<ContentControl>("MonthHost")!; Assert.Empty(month.GetVisualDescendants().OfType<WeekDayTimeline>()); var card = Assert.Single(month.GetVisualDescendants().OfType<ScheduledTaskCard>());
            card.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Null(task.ScheduledStart); Assert.Contains(vm.Data.Tasks, t => t.Id == task.Id);
        }
        finally { window.Close(); }
    }
}
