namespace DbInda.Worker.Configuration;

public sealed class LoggingOptions
{
    public const string SectionName = "Logging";

    public int RetainedDays { get; set; } = 31;
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;
}
