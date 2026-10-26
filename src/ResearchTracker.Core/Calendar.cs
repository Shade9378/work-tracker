namespace ResearchTracker.Core;

public sealed record CalendarEvent
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    public bool Busy { get; init; } = true;
    public Guid? TaskId { get; init; }
    public bool Overlaps(DateTime start, DateTime end) => Start < end && End > start;
}

public static class CalendarDates
{
    public static DateTime Monday(DateTime day) => day.Date.AddDays(-((int)day.DayOfWeek + 6) % 7);
    public static DateTime MonthStart(DateTime day) => new(day.Year, day.Month, 1);
    public static IReadOnlyList<DateTime> MonthCells(DateTime month) => Enumerable.Range(0, 42).Select(i => Monday(MonthStart(month)).AddDays(i)).ToArray();
}
