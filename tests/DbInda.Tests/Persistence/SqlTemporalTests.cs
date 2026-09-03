using DbInda.Worker.Persistence;

namespace DbInda.Tests.Persistence;

public sealed class SqlTemporalTests
{
    [Fact]
    public void DateOnly_valido_se_convierte_a_DateTime_sin_alterar_la_fecha()
    {
        var date = new DateOnly(2026, 8, 15);
        var converted = SqlTemporal.ToDbDate(date);

        Assert.Equal(2026, converted.Year);
        Assert.Equal(8, converted.Month);
        Assert.Equal(15, converted.Day);
        Assert.Equal(0, converted.Hour);
        Assert.Equal(0, converted.Minute);
        Assert.Equal(0, converted.Second);
        Assert.Equal(0, converted.Millisecond);
        Assert.Equal(DateTimeKind.Unspecified, converted.Kind);
        Assert.Equal(date, DateOnly.FromDateTime(converted));
    }

    [Fact]
    public void TimeOnly_valido_se_convierte_a_TimeSpan_sin_perder_la_hora()
    {
        var time = new TimeOnly(10, 7, 59);
        var converted = SqlTemporal.ToDbTime(time);

        Assert.Equal(10, converted.Hours);
        Assert.Equal(7, converted.Minutes);
        Assert.Equal(59, converted.Seconds);
        Assert.Equal(0, converted.Milliseconds);
        Assert.Equal(time, TimeOnly.FromTimeSpan(converted));
    }

    [Fact]
    public void DateOnly_null_permanece_null()
    {
        DateOnly? value = null;
        Assert.Null(SqlTemporal.ToDbDate(value));
    }

    [Fact]
    public void TimeOnly_null_permanece_null()
    {
        TimeOnly? value = null;
        Assert.Null(SqlTemporal.ToDbTime(value));
    }
}
