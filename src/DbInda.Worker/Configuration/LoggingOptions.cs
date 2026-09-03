namespace DbInda.Worker.Configuration;

public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    public int RetainedDays { get; set; } = 31;
}
