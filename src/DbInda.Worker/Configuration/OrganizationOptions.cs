namespace DbInda.Worker.Configuration;

public sealed class OrganizationOptions
{
    public bool Enabled { get; set; } = true;
    // Empty paths are resolved underneath Paths:Input.
    public string Inbox { get; set; } = "";
    public string Organized { get; set; } = "";
    public string Logs { get; set; } = "";
    public int ScanIntervalSeconds { get; set; } = 10;
    public int ReadinessTimeoutSeconds { get; set; } = 10;
}