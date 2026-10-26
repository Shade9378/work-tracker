namespace ResearchTracker.Core;

public static class TaskSchedule
{
    public static IEnumerable<TaskOccurrence> Occurrences(WorkTask task) => (task.ScheduledStart is { } start ? new[] { new TaskOccurrence(start, task.Manual) } : []).Concat(task.AdditionalSchedules).OrderBy(s => s.Start);
    public static void Set(WorkTask task, DateTime start, bool manual)
    {
        if (task.ScheduledStart == null || task.ScheduledStart.Value.Date == start.Date) { task.ScheduledStart = start; task.Manual = manual; return; }
        task.AdditionalSchedules.RemoveAll(s => s.Start.Date == start.Date); task.AdditionalSchedules.Add(new(start, manual));
    }
    public static void Remove(WorkTask task, DateTime? start = null)
    {
        if (start == null) { task.AdditionalSchedules.Clear(); task.ScheduledStart = null; task.Manual = false; return; }
        if (task.ScheduledStart == start)
        {
            var next = task.AdditionalSchedules.OrderBy(s => s.Start).FirstOrDefault();
            task.ScheduledStart = next?.Start; task.Manual = next?.Manual ?? false; if (next != null) task.AdditionalSchedules.Remove(next);
        }
        else task.AdditionalSchedules.RemoveAll(s => s.Start == start);
    }
    public static IEnumerable<(WorkTask Task, TaskOccurrence Schedule)> OnDay(IEnumerable<WorkTask> tasks, DateTime day) => tasks.SelectMany(t => Occurrences(t).Where(s => s.Start < day.Date.AddDays(1) && s.Start.AddHours(t.EffortHours) > day.Date).Select(s => (Task: t, Schedule: s))).OrderBy(s => s.Schedule.Start);
    public static string Placement(WorkTask task, IReadOnlyCollection<string> conflicts)
    {
        if (task.Status == WorkStatus.Done) return "DONE - " + (task.CompletedAt is { } completed ? completed.LocalDateTime.ToString("HH:mm - MMM d, yyyy") : "time not recorded");
        if (task.Status == WorkStatus.Cancelled) return "CANCELLED";
        if (conflicts.Count > 0) return "CONFLICT - " + string.Join("; ", conflicts);
        return Description(task).Replace("Unscheduled", "UNSCHEDULED").Replace("Scheduled", "SCHEDULED").Replace(" · ", " - ");
    }
    public static IReadOnlyList<DateTime> Days(WorkTask task)
    {
        return Occurrences(task).SelectMany(s => { var last = s.Start.AddHours(task.EffortHours).AddTicks(-1).Date; return Enumerable.Range(0, (last - s.Start.Date).Days + 1).Select(i => s.Start.Date.AddDays(i)); }).Distinct().Order().ToArray();
    }
    public static string Description(WorkTask task)
    {
        var days = Days(task); return days.Count == 0 ? "Unscheduled" : $"Scheduled · {days.Count} day{(days.Count == 1 ? "" : "s")} · " + string.Join(", ", days.Select(d => d.ToString("ddd, MMM d")));
    }
}
