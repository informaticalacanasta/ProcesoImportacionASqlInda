namespace DbInda.Worker.Persistence;

public static class SqlTemporal
{
    public static DateTime? ToDbDate(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);

    public static DateTime ToDbDate(DateOnly value)
        => value.ToDateTime(TimeOnly.MinValue);

    public static TimeSpan? ToDbTime(TimeOnly? value)
        => value?.ToTimeSpan();

    public static TimeSpan ToDbTime(TimeOnly value)
        => value.ToTimeSpan();
}
