namespace DbInda.Worker.Configuration;

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    public int InitialDelaySeconds { get; set; } = 5;
    public int MaxDelaySeconds { get; set; } = 300;
    public double BackoffMultiplier { get; set; } = 2;

    public int InitialSeconds
    {
        get => InitialDelaySeconds;
        set => InitialDelaySeconds = value;
    }

    public int MaxSeconds
    {
        get => MaxDelaySeconds;
        set => MaxDelaySeconds = value;
    }
}
