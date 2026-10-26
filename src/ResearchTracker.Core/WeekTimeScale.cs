namespace ResearchTracker.Core;

public static class WeekTimeScale
{
    public const double PixelsPerHour = 64;
    public static DateTime TimeAt(DateTime day, double y)
    {
        var minutes = Math.Clamp(Math.Round(y / PixelsPerHour * 4, MidpointRounding.AwayFromZero) * 15, 0, 24 * 60 - 15);
        return day.Date.AddMinutes(minutes);
    }
    public static double Position(DateTime time) => time.TimeOfDay.TotalHours * PixelsPerHour;
}
