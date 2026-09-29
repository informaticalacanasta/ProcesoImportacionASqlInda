namespace DbInda.Worker.Configuration;

public sealed class OrderOptions
{
    public const string SectionName = "Orders";

    public string Pending { get; set; } = "";
    public string Processed { get; set; } = "";
    public string Errors { get; set; } = "";
    public int MaxConcurrency { get; set; } = 4;
    public int ScanIntervalSeconds { get; set; } = 10;
}
