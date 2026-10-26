using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using ResearchTracker.Application;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class RepeatedSchedulingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerRepeat-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Monday = new(2030, 1, 7);
    private static Workspace Week() => new() { Days = Enumerable.Range(0, 7).Select(i => new DayRule { Date = Monday.AddDays(i) }).ToList() };
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact] public void WeeklyCountAndHoursOverflowAreRejectedBeforeAnyScheduleChanges()
    {
        var data = Week(); var tasks = Enumerable.Range(0, 4).Select(i => new WorkTask { Title = "Task " + i }).ToList(); data.Tasks.AddRange(tasks);
        TaskSchedule.Set(tasks[0], Monday.AddHours(9), true); var before = JsonSerializer.Serialize(data);
        var counts = tasks.ToDictionary(t => t.Id, _ => 7);
        var error = Assert.Throws<InvalidOperationException>(() => new Scheduler().Schedule(data, Monday, requestedCounts: counts));
        Assert.Contains("weekly maximum of 21 tasks", error.Message); Assert.Equal(before, JsonSerializer.Serialize(data));
        foreach (var day in data.Days) day.MaxHours = 1;
        tasks[0].EffortHours = 2; before = JsonSerializer.Serialize(data);
        error = Assert.Throws<InvalidOperationException>(() => new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int> { [tasks[0].Id] = 4 }));
        Assert.Contains("weekly workload maximum of 7 hours", error.Message); Assert.Equal(before, JsonSerializer.Serialize(data));
    }

    [Fact] public void RequestedCountsRepeatAcrossDaysRespectPriorityAndZeroAndPersist()
    {
        var data = Week(); var low = new WorkTask { Title = "Low", Priority = Priority.Low }; var high = new WorkTask { Title = "Urgent", Priority = Priority.Urgent }; var skipped = new WorkTask { Title = "Skip" }; data.Tasks.AddRange([low, high, skipped]);
        var counts = new Dictionary<Guid, int> { [low.Id] = 3, [high.Id] = 7, [skipped.Id] = 0 };
        Assert.Empty(new Scheduler().Schedule(data, Monday, requestedCounts: counts));
        Assert.Equal(3, TaskSchedule.Occurrences(low).Count()); Assert.Equal(7, TaskSchedule.Occurrences(high).Count()); Assert.Empty(TaskSchedule.Occurrences(skipped));
        Assert.Equal(Monday.AddHours(9), high.ScheduledStart); Assert.Equal(Monday.AddHours(10), low.ScheduledStart);
        Assert.All(data.Days, d => Assert.True(TaskSchedule.OnDay(data.Tasks, d.Date).Count() <= 3));
        var store = new WorkspaceStore(root); store.Save(data); Assert.Equal(7, TaskSchedule.Occurrences(store.Load()!.Tasks.Single(t => t.Id == high.Id)).Count());
        TaskSchedule.Remove(high, high.ScheduledStart); Assert.Equal(6, TaskSchedule.Occurrences(high).Count());
        new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int>()); Assert.All(data.Tasks, t => Assert.Empty(TaskSchedule.Occurrences(t)));
    }

    [Fact] public void CapacityExcludesUnavailableDaysAndUsesActualWorkingHours()
    {
        var data = Week(); data.Days[0].Unavailable = true; data.Days[1].StartHour = 9; data.Days[1].EndHour = 11; data.Days[1].MaxHours = 6;
        Assert.Equal((18, 32d), Scheduler.WeeklyCapacity(data, Monday));
        Assert.All(data.Days, d => Assert.Equal(3, d.MaxTasks));
    }

    [Fact] public void DeadlineCapsCountsAndRejectsExcessBeforeChangingSchedules()
    {
        var data = Week(); var task = new WorkTask { Title = "Due Wednesday", Deadline = new DateTimeOffset(Monday.AddDays(2).AddHours(17)) }; data.Tasks.Add(task);
        Assert.Equal(3, Scheduler.DeadlineCountLimit(data, task, Monday));
        TaskSchedule.Set(task, Monday.AddHours(9), true); var before = JsonSerializer.Serialize(data);
        var error = Assert.Throws<InvalidOperationException>(() => new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int> { [task.Id] = 7 }));
        Assert.Contains("maximum 3 placements", error.Message); Assert.Equal(before, JsonSerializer.Serialize(data));
        Assert.Empty(new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int> { [task.Id] = 3 })); Assert.Equal(3, TaskSchedule.Occurrences(task).Count());
        task.Deadline = new DateTimeOffset(Monday.AddDays(2).AddHours(9.5)); Assert.Equal(2, Scheduler.DeadlineCountLimit(data, task, Monday));
        task.Deadline = new DateTimeOffset(Monday.AddDays(2).AddHours(10)); Assert.Equal(3, Scheduler.DeadlineCountLimit(data, task, Monday));
        task.Deadline = new DateTimeOffset(Monday.AddDays(-1)); Assert.Equal(0, Scheduler.DeadlineCountLimit(data, task, Monday));
        task.Deadline = null; Assert.Equal(7, Scheduler.DeadlineCountLimit(data, task, Monday));
        task.Deadline = new DateTimeOffset(Monday.AddDays(10)); Assert.Equal(7, Scheduler.DeadlineCountLimit(data, task, Monday));
    }

    [Fact] public void DeadlineLimitAccountsForRemainingTimeAndPreservedPastPlacements()
    {
        var data = Week(); var task = new WorkTask { Deadline = new DateTimeOffset(Monday.AddDays(2).AddHours(17)) }; data.Tasks.Add(task);
        var now = Monday.AddDays(1).AddHours(16.1);
        Assert.Equal(1, Scheduler.DeadlineCountLimit(data, task, Monday, now));
        TaskSchedule.Set(task, Monday.AddHours(9), true); Assert.Equal(2, Scheduler.DeadlineCountLimit(data, task, Monday, now));
        Assert.Equal(1, Scheduler.DeadlineCountLimit(data, task, Monday, Monday.AddDays(7)));
    }

    [AvaloniaFact] public void AutoAssignCounterMaximumAdaptsToDeadlineWhenOpened()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); vm.ShowCalendarDate(Monday); var task = new WorkTask { Title = "Due Wednesday", Deadline = new DateTimeOffset(Monday.AddDays(2).AddHours(17)) }; vm.Data.Tasks.Add(task); vm.Refresh();
        foreach (var expected in new[] { 3m, 0m, 7m })
        {
            var dialog = new AutoAssignWindow(vm); dialog.Show();
            try
            {
                var counter = Assert.Single(dialog.GetVisualDescendants().OfType<NumericUpDown>()); Assert.Equal(expected, counter.Maximum); Assert.Equal(0m, counter.Value);
            }
            finally { dialog.Close(); }
            task.Deadline = expected == 3m ? new DateTimeOffset(Monday.AddDays(-1)) : null;
        }
    }

    [Fact] public void RepeatsRespectDependenciesAndReportRequestsThatCannotFitIndividualDays()
    {
        var data = Week(); var prerequisite = new WorkTask { Title = "Prerequisite", Priority = Priority.Low }; var dependent = new WorkTask { Title = "Dependent", Priority = Priority.Urgent, Dependencies = [prerequisite.Id] }; data.Tasks.AddRange([prerequisite, dependent]);
        Assert.Empty(new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int> { [prerequisite.Id] = 2, [dependent.Id] = 2 }));
        Assert.All(TaskSchedule.Occurrences(dependent), s => Assert.Contains(TaskSchedule.Occurrences(prerequisite), p => p.Start.AddHours(prerequisite.EffortHours) <= s.Start));
        foreach (var day in data.Days.Skip(1)) day.AllowedProjects = [Guid.NewGuid()];
        var issues = new Scheduler().Schedule(data, Monday, requestedCounts: new Dictionary<Guid, int> { [prerequisite.Id] = 2 });
        Assert.Single(TaskSchedule.Occurrences(prerequisite)); Assert.Contains(issues, i => i.TaskId == prerequisite.Id && i.Message.Contains("1 of 2"));
    }

    [Fact] public void SharedCliAndMcpCommandsRejectOverflowAtomicallyAndExposeRepeatedDays()
    {
        var data = Week(); var task = new WorkTask { Title = "Repeat" }; data.Tasks.Add(task); var store = new WorkspaceStore(root); store.Save(data); var commands = new WorkspaceCommands(root);
        var result = commands.planner_schedule("2030-01-07", new Dictionary<Guid, int> { [task.Id] = 3 }); Assert.Single(result.GetProperty("tasks").EnumerateArray());
        Assert.Equal(3, TaskSchedule.Occurrences(store.Load()!.Tasks.Single()).Count());
        commands.tasks_schedule(task.Id, "2030-01-11 11:00"); Assert.Equal(4, TaskSchedule.Occurrences(store.Load()!.Tasks.Single()).Count());
        commands.tasks_schedule(task.Id, removeStart: "2030-01-11 11:00"); Assert.Equal(3, TaskSchedule.Occurrences(store.Load()!.Tasks.Single()).Count());
        foreach (var day in data.Days) commands.planner_rule(day.Date.ToString("yyyy-MM-dd"), maxHours: 0);
        var before = JsonSerializer.Serialize(store.Load());
        Assert.Throws<InvalidOperationException>(() => commands.planner_schedule("2030-01-07", new Dictionary<Guid, int> { [task.Id] = 1 })); Assert.Equal(before, JsonSerializer.Serialize(store.Load()));
    }

    [Fact] public void LegacyFiveTaskDefaultsMigrateToThreeButOtherLimitsAndFutureCustomFiveRemain()
    {
        var store = new WorkspaceStore(root); var data = Week(); data.Version = 3; data.Days[0].MaxTasks = 5; data.Days[1].MaxTasks = 2;
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO workspace VALUES(1,3,$json,'test')"; command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(data)); command.ExecuteNonQuery();
        }
        data = store.Load()!; Assert.Equal(4, data.Version); Assert.Equal(3, data.Days[0].MaxTasks); Assert.Equal(2, data.Days[1].MaxTasks);
        store.Save(data); Assert.Single(Directory.GetFiles(root, "before-schema-v4-*.db")); data.Days[0].MaxTasks = 5; store.Save(data); Assert.Equal(5, store.Load()!.Days[0].MaxTasks);
    }

    [AvaloniaFact] public void AutoAssignDialogBlocksOverflowAllowsZeroAndTrayRetainsRepeatedTasks()
    {
        var vm = new MainViewModel(new WorkspaceStore(root)); vm.ShowCalendarDate(Monday);
        for (var i = 0; i < 4; i++) vm.Data.Tasks.Add(new WorkTask { Title = "Task " + i });
        foreach (var day in vm.Days) day.MaxHours = 1;
        vm.ManualSchedule(vm.Data.Tasks[0], Monday.AddHours(9)); vm.Refresh();
        var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("AutoAssignButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<AutoAssignWindow>(Assert.Single(window.OwnedWindows)); var counters = dialog.GetVisualDescendants().OfType<NumericUpDown>().ToArray(); Assert.Equal(4, counters.Length);
            Assert.All(counters, counter => Assert.Equal(0m, counter.Value));
            var total = dialog.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "AssignTotalHours"); Assert.Equal("Total hours: 0", total.Text);
            var taskTotal = dialog.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "AssignTotalTasks"); Assert.Equal("Total tasks: 0", taskTotal.Text);
            foreach (var counter in counters) counter.Value = 7;
            Assert.Equal("Total tasks: 28", taskTotal.Text); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Danger")).Color, ((ISolidColorBrush)taskTotal.Foreground!).Color);
            counters[3].Value = 0; Assert.Equal("Total tasks: 21", taskTotal.Text); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("TextPrimary")).Color, ((ISolidColorBrush)taskTotal.Foreground!).Color);
            Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Danger")).Color, ((ISolidColorBrush)total.Foreground!).Color); counters[3].Value = 7;
            Assert.Equal("Total hours: 28", total.Text); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("Danger")).Color, ((ISolidColorBrush)total.Foreground!).Color);
            Assert.False(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAutoAssign").IsEnabled); Assert.Contains("weekly maximum", dialog.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "AssignCountsError").Text);
            foreach (var counter in counters) counter.Value = 0;
            counters[0].Value = 2; Assert.True(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAutoAssign").IsEnabled);
            Assert.Equal("Total hours: 2", total.Text); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("TextPrimary")).Color, ((ISolidColorBrush)total.Foreground!).Color);
            Assert.Equal("Total tasks: 2", taskTotal.Text); Assert.Equal(((ISolidColorBrush)DesignTokens.Brush("TextPrimary")).Color, ((ISolidColorBrush)taskTotal.Foreground!).Color);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmAutoAssign").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); Assert.Empty(window.OwnedWindows);
            var task = vm.Data.Tasks.Single(t => TaskSchedule.Occurrences(t).Count() == 2);
            Assert.Equal(4, window.FindControl<StackPanel>("Backlog")!.Children.Count);
            Assert.Equal(2, window.GetVisualDescendants().OfType<ScheduledTaskCard>().Count(c => c.Name == "ScheduledTask" + task.Id.ToString("N")));
            var old = TaskSchedule.Occurrences(task).First().Start; vm.ManualSchedule(task, Monday.AddDays(4).AddHours(11), old); Assert.Equal(2, TaskSchedule.Occurrences(task).Count()); Assert.DoesNotContain(TaskSchedule.Occurrences(task), s => s.Start == old);
            vm.Undo(); Assert.Contains(TaskSchedule.Occurrences(vm.Data.Tasks.Single(t => t.Id == task.Id)), s => s.Start == old);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); }
    }
}
