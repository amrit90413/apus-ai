namespace Gateway.Api.Allowances;

/// <summary>
/// Calendar month boundaries in UTC. Periods are explicit rows keyed on their start,
/// so "the monthly reset" is opening the next period — never zeroing a counter, which
/// would race with in-flight requests and erase history.
/// </summary>
public static class AllowanceCalendar
{
    public static DateTimeOffset StartOf(DateTimeOffset at) =>
        new(at.UtcDateTime.Year, at.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset EndOf(DateTimeOffset at) => StartOf(at).AddMonths(1);

    public static (DateTimeOffset start, DateTimeOffset end) Current(DateTimeOffset at) => (StartOf(at), EndOf(at));

    public static (DateTimeOffset start, DateTimeOffset end) Next(DateTimeOffset at)
    {
        var start = StartOf(at).AddMonths(1);
        return (start, start.AddMonths(1));
    }

    /// <summary>Day boundaries, for the optional daily sub-limit.</summary>
    public static (DateTimeOffset start, DateTimeOffset end) Day(DateTimeOffset at)
    {
        var start = new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero);
        return (start, start.AddDays(1));
    }
}
