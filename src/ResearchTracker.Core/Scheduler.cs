namespace ResearchTracker.Core;

public record ScheduleIssue(Guid TaskId, string Message);
public class Scheduler
{
    public IReadOnlyList<CalendarEvent> CalendarEvents { get; set; } = [];
    private static bool Active(WorkTask t) => t.Status is WorkStatus.Todo or WorkStatus.InProgress;
    public static (int Tasks, double Hours) WeeklyCapacity(Workspace data, DateTime weekStart)
    {
        var days = data.Days.Where(d => d.Date >= weekStart.Date && d.Date < weekStart.Date.AddDays(7) && ValidRule(d) && !d.Unavailable).ToList();
        return (days.Sum(d => d.MaxTasks), days.Sum(d => Math.Min(d.MaxHours, d.EndHour - d.StartHour)));
    }
    public static int DeadlineCountLimit(Workspace data, WorkTask task, DateTime weekStart, DateTime? notBefore = null)
    {
        if (!Active(task) || !double.IsFinite(task.EffortHours) || task.EffortHours <= 0 || task.EffortHours > 168) return 0;
        var first = weekStart.Date; var earliest = notBefore ?? first; var deadline = task.Deadline?.LocalDateTime ?? DateTime.MaxValue;
        var preserved = TaskSchedule.Occurrences(task).Where(s => s.Start >= first && s.Start < first.AddDays(7) && s.Start < earliest).Select(s => s.Start.Date).ToHashSet();
        return Enumerable.Range(0, 7).Count(i =>
        {
            var date = first.AddDays(i); if (preserved.Contains(date)) return true;
            var rule = data.Days.FirstOrDefault(d => d.Date.Date == date) ?? new DayRule { Date = date };
            if (!ValidRule(rule)) return false;
            var start = date.AddHours(rule.StartHour);
            if (start < earliest) start = start.AddMinutes(Math.Ceiling((earliest - start).TotalMinutes / 15) * 15);
            var finish = start.AddHours(task.EffortHours);
            return finish <= date.AddHours(rule.EndHour) && finish <= deadline;
        });
    }
    public static List<string> ValidateCounts(Workspace data, DateTime weekStart, IReadOnlyDictionary<Guid, int> counts, DateTime? notBefore = null)
    {
        var errors = new List<string>();
        if (counts.Any(p => p.Value < 0 || p.Value > 7 || data.Tasks.All(t => t.Id != p.Key))) return ["Use whole counts from 0 to 7 for existing tasks."];
        if (counts.Any(p => p.Value > 0 && !Active(data.Tasks.Single(t => t.Id == p.Key)))) errors.Add("Only open tasks can be assigned.");
        foreach (var pair in counts.Where(p => p.Value > 0))
        {
            var task = data.Tasks.Single(t => t.Id == pair.Key); if (!Active(task)) continue;
            var limit = DeadlineCountLimit(data, task, weekStart, notBefore);
            if (pair.Value > limit) errors.Add($"{task.Title}: maximum {limit} placements before its deadline in this week and remaining time.");
        }
        var capacity = WeeklyCapacity(data, weekStart); var requested = counts.Values.Sum(); var hours = counts.Sum(p => p.Value * data.Tasks.Single(t => t.Id == p.Key).EffortHours);
        if (requested > capacity.Tasks) errors.Add($"Requested {requested} placements exceeds the weekly maximum of {capacity.Tasks} tasks.");
        if (hours > capacity.Hours + .00001) errors.Add($"Requested {hours:0.##} hours exceeds the weekly workload maximum of {capacity.Hours:0.##} hours.");
        return errors;
    }
    public List<ScheduleIssue> Schedule(Workspace data, DateTime weekStart, DateTime? notBefore = null, IReadOnlyDictionary<Guid, int>? requestedCounts = null)
    {
        var first = weekStart.Date; var end = first.AddDays(7); var earliest = notBefore ?? first;
        if (requestedCounts != null && requestedCounts.Any(p => p.Value < 0 || p.Value > 7 || data.Tasks.All(t => t.Id != p.Key))) throw new ArgumentException("Schedule counts must be 0 through 7 for existing tasks.");
        if (requestedCounts != null) { var errors = ValidateCounts(data, first, requestedCounts, earliest); if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors)); }
        var quotas = data.Tasks.Where(Active).ToDictionary(t => t.Id, t => requestedCounts == null ? 1 : requestedCounts.GetValueOrDefault(t.Id));
        foreach (var task in data.Tasks.Where(Active))
            foreach (var occurrence in TaskSchedule.Occurrences(task).Where(s => (requestedCounts != null || !s.Manual) && s.Start >= first && s.Start < end && s.Start >= earliest).ToArray()) TaskSchedule.Remove(task, occurrence.Start);
        int Count(WorkTask task) => TaskSchedule.Occurrences(task).Count(s => s.Start >= first && s.Start < end);
        var pending = data.Tasks.Where(Active).Where(t => quotas[t.Id] > 0).OrderByDescending(t => t.Priority).ThenBy(t => t.Deadline ?? DateTimeOffset.MaxValue).ThenByDescending(t => t.EffortHours).ThenBy(t => t.Id).ToArray();
        bool progress;
        do
        {
            progress = false;
            foreach (var task in pending)
            {
                if (!double.IsFinite(task.EffortHours) || task.EffortHours <= 0 || task.EffortHours > 168) continue;
                while (Count(task) < quotas[task.Id])
                {
                    DateTime? placement = null;
                    foreach (var day in data.Days.Where(d => d.Date >= first && d.Date < end).OrderBy(d => d.Date))
                    {
                        if (!ValidRule(day) || day.Unavailable || TaskSchedule.Occurrences(task).Any(s => s.Start.Date == day.Date)) continue;
                        for (var start = day.Date.AddHours(day.StartHour); start.AddHours(task.EffortHours) <= day.Date.AddHours(day.EndHour); start = start.AddMinutes(15))
                            if (start >= earliest && Conflicts(data, task, start).Count == 0) { placement = start; break; }
                        if (placement != null) break;
                    }
                    if (placement is not { } time) break;
                    TaskSchedule.Set(task, time, false); progress = true;
                }
            }
        } while (progress);
        var issues = Validate(data, first);
        issues.AddRange(pending.Where(t => Count(t) < quotas[t.Id]).Select(t => new ScheduleIssue(t.Id, $"Unscheduled: placed {Count(t)} of {quotas[t.Id]} requested; no valid slot (capacity, deadline, project or dependency constraint).")));
        return issues;
    }
    public static bool ValidRule(DayRule d) => double.IsFinite(d.StartHour) && double.IsFinite(d.EndHour) && double.IsFinite(d.MaxHours) && d.StartHour >= 0 && d.EndHour <= 24 && d.StartHour < d.EndHour && d.MaxHours >= 0 && d.MaxTasks >= 0;
    public List<string> Conflicts(Workspace data, WorkTask task) => TaskSchedule.Occurrences(task).SelectMany(s => Conflicts(data, task, s.Start).Select(message => TaskSchedule.Occurrences(task).Skip(1).Any() ? s.Start.ToString("ddd, MMM d HH:mm") + ": " + message : message)).Distinct().ToList();
    public List<string> Conflicts(Workspace data, WorkTask task, DateTime start)
    {
        List<string> issues = [];
        if (!double.IsFinite(task.EffortHours) || task.EffortHours <= 0 || task.EffortHours > 168) return ["Effort must be greater than zero and at most 168 hours."];
        var finish = start.AddHours(task.EffortHours); var rule = data.Days.FirstOrDefault(d => d.Date.Date == start.Date);
        if (rule == null || !ValidRule(rule)) return ["Missing or invalid day constraints."];
        if (rule.Unavailable) issues.Add("Day is unavailable.");
        if (rule.AllowedProjects.Count != 0 && (task.ProjectId is not { } projectId || !rule.AllowedProjects.Contains(projectId))) issues.Add("Project is not allowed on this day.");
        if (start < start.Date.AddHours(rule.StartHour) || finish > start.Date.AddHours(rule.EndHour)) issues.Add("Outside working hours.");
        var others = TaskSchedule.OnDay(data.Tasks.Where(Active), start.Date).Where(s => s.Task.Id != task.Id || s.Schedule.Start.Date != start.Date).ToList();
        if (others.Count >= rule.MaxTasks) issues.Add("Maximum task count exceeded.");
        if (others.Sum(s => s.Task.EffortHours) + task.EffortHours > rule.MaxHours + .00001) issues.Add("Workload limit exceeded.");
        if (others.Any(s => s.Schedule.Start < finish && s.Schedule.Start.AddHours(s.Task.EffortHours) > start)) issues.Add("Overlaps another task.");
        if (CalendarEvents.Any(e => e.Busy && e.TaskId != task.Id && e.Overlaps(start, finish))) issues.Add("Overlaps a Google Calendar event.");
        if (task.Deadline != null && finish > task.Deadline.Value.LocalDateTime) issues.Add("Finishes after deadline.");
        foreach (var id in task.Dependencies)
        {
            var dep = data.Tasks.FirstOrDefault(t => t.Id == id);
            if (dep == null || dep.Status != WorkStatus.Done && (dep.Status == WorkStatus.Cancelled || !TaskSchedule.Occurrences(dep).Any(s => s.Start.AddHours(dep.EffortHours) <= start))) issues.Add("Dependency must finish first.");
        }
        return issues.Distinct().ToList();
    }
    public List<ScheduleIssue> Validate(Workspace data, DateTime weekStart) => data.Tasks.Where(Active)
        .SelectMany(t => TaskSchedule.Occurrences(t).Where(s => s.Start >= weekStart.Date && s.Start < weekStart.Date.AddDays(7)).SelectMany(s => Conflicts(data, t, s.Start).Select(message => new ScheduleIssue(t.Id, s.Start.ToString("ddd HH:mm") + ": " + message)))).ToList();
}
