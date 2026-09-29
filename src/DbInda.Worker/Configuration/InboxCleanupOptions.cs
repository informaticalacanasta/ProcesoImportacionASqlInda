namespace DbInda.Worker.Configuration;

public sealed class InboxCleanupOptions
{
    public const string SectionName = "InboxCleanup";

    public bool Enabled { get; set; } = true;

    public int RetentionHours { get; set; } = 24;

    public int ScanIntervalMinutes { get; set; } = 10;
}
