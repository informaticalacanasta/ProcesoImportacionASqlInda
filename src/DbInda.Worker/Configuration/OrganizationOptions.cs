namespace DbInda.Worker.Configuration;

public sealed class OrganizationOptions
{
    public bool Enabled { get; set; } = true;
    // Empty paths are resolved underneath Paths:Input.
    public string Inbox { get; set; } = "";
    public string Organized { get; set; } = "";
    public string Logs { get; set; } = "";
    public int MaxConcurrency { get; set; } = 4;
    // Pause after a pass finishes. Passes do not overlap, so the period is pass duration plus this interval.
    public int ScanIntervalSeconds { get; set; } = 2;
    public int ReadinessTimeoutSeconds { get; set; } = 10;
}