using DbInda.Worker.Configuration;

namespace DbInda.Worker.Tracking;

public static class ExpectedSourceSchedule
{
    public static bool IsAlertDue(
        ExpectedSourceOptions source,
        TimeZoneInfo zone,
        DateTimeOffset nowUtc,
        DateTimeOffset? lastSuccessUtc)
    {
        if (source.ActiveDays.Count == 0 || source.MaxSilenceMinutes < 1)
            return false;
        if (!TimeOnly.TryParse(source.Start, out var start) || !TimeOnly.TryParse(source.End, out var end))
            return false;

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        if (!source.ActiveDays.Any(day => Enum.TryParse<DayOfWeek>(day, true, out var parsed) && parsed == local.DayOfWeek))
            return false;
        var open = local.Date + start.ToTimeSpan();
        var close = local.Date + end.ToTimeSpan();
        var graceEnd = open.AddMinutes(source.GraceMinutes);
        if (local.DateTime < graceEnd || local.DateTime >= close)
            return false;

        if (lastSuccessUtc is null)
            return true;

        var silence = nowUtc - lastSuccessUtc.Value;
        return silence >= TimeSpan.FromMinutes(source.MaxSilenceMinutes);
    }
}

public static class HealthJudgement
{
    public const string Sano = "SANO";
    public const string Degradado = "DEGRADADO";
    public const string Bloqueado = "BLOQUEADO";
    public const string EsperandoDatos = "ESPERANDO_DATOS";

    public static string Judge(HealthSnapshot snapshot)
    {
        if (!snapshot.InputReachable || snapshot.SqlDownSustained || snapshot.PendingStalled || snapshot.OutboxSaturated)
            return Bloqueado;
        if (!snapshot.SqlReachable || snapshot.RetryWaiting > 0 || snapshot.OutboxPending > 0 || snapshot.ArchivesPending > 0)
            return Degradado;
        if (snapshot.PendingOnDisk == 0 && snapshot.QueueCount == 0 && snapshot.InFlightCount == 0)
            return EsperandoDatos;
        return Sano;
    }
}

public sealed class HealthSnapshot
{
    public bool ProcessAlive { get; init; } = true;
    public string ProcessingState { get; init; } = HealthJudgement.EsperandoDatos;
    public bool SqlReachable { get; init; }
    public bool SqlDownSustained { get; init; }
    public DateTimeOffset? SqlUnreachableSinceUtc { get; init; }
    public bool InputReachable { get; init; }
    public int QueueCount { get; init; }
    public int InFlightCount { get; init; }
    public int PendingOnDisk { get; init; }
    public DateTimeOffset? OldestPendingUtc { get; init; }
    public bool PendingStalled { get; init; }
    public int RetryWaiting { get; init; }
    public int ArchivesPending { get; init; }
    public int OutboxPending { get; init; }
    public DateTimeOffset? OldestOutboxUtc { get; init; }
    public bool OutboxSaturated { get; init; }
    public DateTimeOffset? LastScanUtc { get; init; }
    public bool LastScanOk { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public Guid ExecutionId { get; init; }
}
