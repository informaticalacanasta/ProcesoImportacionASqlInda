namespace DbInda.Worker.Configuration;

public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    public int MaxConcurrency { get; set; } = 20;
    public int QueueCapacity { get; set; } = 100;
    public int ScanIntervalSeconds { get; set; } = 10;
    public int StableChecks { get; set; } = 3;
    public int StableCheckDelayMilliseconds { get; set; } = 1000;
}
