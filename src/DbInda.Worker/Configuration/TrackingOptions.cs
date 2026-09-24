namespace DbInda.Worker.Configuration;

public sealed class TrackingOptions
{
    public const string SectionName = "Tracking";

    public string InstanceName { get; set; } = "";
    public string OutboxDirectory { get; set; } = "";
    public int HeartbeatSeconds { get; set; } = 60;
    public int StaleSignalMinutes { get; set; } = 3;
    public int SqlDownMinutes { get; set; } = 5;
    public int PendingWithoutProgressMinutes { get; set; } = 10;
    public int PendingMaxAgeMinutes { get; set; } = 30;
    public int ArchiveStuckMinutes { get; set; } = 15;
    public int OutboxMaxAgeMinutes { get; set; } = 10;
    public int OutboxMaxFiles { get; set; } = 10000;
    public long OutboxMaxBytes { get; set; } = 52_428_800;
    public int ReminderMinutes { get; set; } = 30;
    public string BusinessTimeZone { get; set; } = "Europe/Madrid";
    public TrackingRetentionOptions Retention { get; set; } = new();
    public TrackingAlertOptions Alerts { get; set; } = new();
    public List<ExpectedSourceOptions> ExpectedSources { get; set; } = [];
}

public sealed class TrackingRetentionOptions
{
    public bool Enabled { get; set; }
    public int HistoryDays { get; set; }
    public int BatchSize { get; set; } = 500;
}

public sealed class TrackingAlertOptions
{
    public bool WebhookEnabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    public int RetrySeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
}

public sealed class ExpectedSourceOptions
{
    public int Tienda { get; set; }
    public int Tpv { get; set; }
    public List<string> ActiveDays { get; set; } = [];
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "22:00";
    public int GraceMinutes { get; set; } = 60;
    public int MaxSilenceMinutes { get; set; } = 120;
}
