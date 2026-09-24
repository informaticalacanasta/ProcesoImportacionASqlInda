using System.Text.Json;

namespace DbInda.Worker.Alerts;

public static class AlertChannels
{
    public const string Local = "local";
    public const string Webhook = "webhook";
}

public enum AlertChannelUpdate
{
    Unchanged,
    Delivered,
    Scheduled,
    Abandoned
}

public sealed class AlertDeliveryPolicy
{
    public bool WebhookEnabled { get; init; }
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed class AlertDeliveryState
{
    public string Channel { get; set; } = "";
    public string NoticeId { get; set; } = "";
    public string State { get; set; } = "pendiente";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
}

public sealed class AlertIncident
{
    public string Key { get; set; } = "";
    public string Severity { get; set; } = "Warning";
    public string Summary { get; set; } = "";
    public bool Open { get; set; }
    public bool RecoveryPending { get; set; }
    public bool PendingNotify { get; set; }
    public string NoticeId { get; set; } = "";
    public List<AlertDeliveryState> Deliveries { get; set; } = [];
    public DateTimeOffset OpenedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? LastNotifiedUtc { get; set; }
}

public sealed record AlertSignal(string Key, string Severity, string Summary);

public readonly record struct RuleResult(string Key, bool Evaluated, AlertSignal? Signal);

public sealed record AlertNotice(
    string Key,
    string Status,
    string Severity,
    string Summary,
    DateTimeOffset OpenedUtc,
    DateTimeOffset AtUtc,
    string NoticeId = "",
    IReadOnlyList<string>? ChannelsDue = null);

public sealed class AlertStateStore
{
    private readonly string _path;
    private readonly ILogger<AlertStateStore> _logger;

    public AlertStateStore(string path, ILogger<AlertStateStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public Dictionary<string, AlertIncident> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, AlertIncident>(StringComparer.Ordinal);
            var items = JsonSerializer.Deserialize<List<AlertIncident>>(File.ReadAllText(_path)) ?? [];
            return items.Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(item => item.Key, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "El estado de alertas {Path} no se pudo leer. Un reinicio puede repetir un aviso ya enviado.", _path);
            return new Dictionary<string, AlertIncident>(StringComparer.Ordinal);
        }
    }

    public void Save(IEnumerable<AlertIncident> incidents)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, incidents.ToList());
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "No se pudo guardar el estado de alertas en {Path}.", _path);
        }
    }
}

public sealed class AlertEngine
{
    private readonly Dictionary<string, AlertIncident> _incidents;
    private readonly AlertStateStore _store;

    public AlertEngine(AlertStateStore store)
    {
        _store = store;
        _incidents = store.Load();
        foreach (var incident in _incidents.Values)
        {
            if (incident.Deliveries.Count > 0 || !incident.PendingNotify)
                continue;
            if (string.IsNullOrEmpty(incident.NoticeId))
                incident.NoticeId = Guid.NewGuid().ToString("N");
            incident.Deliveries.Add(new AlertDeliveryState
            {
                Channel = AlertChannels.Local,
                NoticeId = incident.NoticeId,
                State = "pendiente"
            });
        }
    }

    public IReadOnlyCollection<AlertIncident> Incidents => _incidents.Values;

    public IReadOnlyList<AlertNotice> Plan(IEnumerable<RuleResult> rules, DateTimeOffset nowUtc, TimeSpan reminder, AlertDeliveryPolicy? policy = null)
    {
        var notices = new List<AlertNotice>();
        foreach (var rule in rules)
        {
            if (!rule.Evaluated)
                continue;

            _incidents.TryGetValue(rule.Key, out var existing);
            if (rule.Signal is null)
            {
                if (existing is { Open: true })
                {
                    existing.Open = false;
                    existing.RecoveryPending = true;
                    existing.UpdatedUtc = nowUtc;
                    existing.LastNotifiedUtc = null;
                    BeginCycle(existing, policy);
                    notices.Add(ToNotice(existing, "recuperada", nowUtc));
                }
                else if (existing is { RecoveryPending: true } && DueChannels(existing, nowUtc).Count > 0)
                {
                    notices.Add(ToNotice(existing, "recuperada", nowUtc));
                }

                continue;
            }

            if (existing is { RecoveryPending: true })
            {
                existing.Open = true;
                existing.RecoveryPending = false;
                existing.Severity = rule.Signal.Severity;
                existing.Summary = rule.Signal.Summary;
                existing.OpenedUtc = nowUtc;
                existing.UpdatedUtc = nowUtc;
                existing.LastNotifiedUtc = null;
                BeginCycle(existing, policy);
                notices.Add(ToNotice(existing, "abierta", nowUtc));
                continue;
            }

            if (existing is null || !existing.Open)
            {
                existing = new AlertIncident
                {
                    Key = rule.Key,
                    Severity = rule.Signal.Severity,
                    Summary = rule.Signal.Summary,
                    Open = true,
                    OpenedUtc = nowUtc,
                    UpdatedUtc = nowUtc
                };
                BeginCycle(existing, policy);
                _incidents[rule.Key] = existing;
                notices.Add(ToNotice(existing, "abierta", nowUtc));
                continue;
            }

            existing.Summary = rule.Signal.Summary;
            existing.Severity = rule.Signal.Severity;
            existing.UpdatedUtc = nowUtc;
            var due = DueChannels(existing, nowUtc);
            if (due.Count > 0)
            {
                notices.Add(ToNotice(existing, existing.LastNotifiedUtc is null ? "abierta" : "reintento", nowUtc));
            }
            else if (existing.LastNotifiedUtc is DateTimeOffset notified && nowUtc - notified >= reminder)
            {
                BeginCycle(existing, policy);
                notices.Add(ToNotice(existing, "recordatorio", nowUtc));
            }
        }

        _store.Save(_incidents.Values);
        return notices;
    }

    public void MarkNotified(string key, DateTimeOffset nowUtc)
    {
        if (!_incidents.TryGetValue(key, out var incident))
            return;
        foreach (var delivery in incident.Deliveries)
        {
            if (delivery.State == "pendiente")
                delivery.State = "enviado";
        }

        incident.PendingNotify = false;
        incident.LastNotifiedUtc = nowUtc;
        if (incident.RecoveryPending)
            _incidents.Remove(key);
        _store.Save(_incidents.Values);
    }

    public AlertChannelUpdate MarkChannel(string key, string noticeId, string channel, bool success, DateTimeOffset nowUtc, AlertDeliveryPolicy policy)
    {
        if (!_incidents.TryGetValue(key, out var incident) || !string.Equals(incident.NoticeId, noticeId, StringComparison.Ordinal))
            return AlertChannelUpdate.Unchanged;

        var delivery = incident.Deliveries.FirstOrDefault(item =>
            item.Channel == channel && string.Equals(item.NoticeId, noticeId, StringComparison.Ordinal));
        if (delivery is null || delivery.State is "enviado" or "abandonado")
            return AlertChannelUpdate.Unchanged;

        AlertChannelUpdate update;
        if (success)
        {
            delivery.State = "enviado";
            delivery.NextAttemptUtc = null;
            update = AlertChannelUpdate.Delivered;
        }
        else
        {
            delivery.Attempts++;
            if (delivery.Attempts >= Math.Max(1, policy.MaxAttempts))
            {
                delivery.State = "abandonado";
                delivery.NextAttemptUtc = null;
                update = AlertChannelUpdate.Abandoned;
            }
            else
            {
                var delay = TimeSpan.FromTicks(Math.Max(policy.RetryDelay.Ticks, TimeSpan.TicksPerSecond) * delivery.Attempts);
                if (policy.MaxDelay > TimeSpan.Zero && delay > policy.MaxDelay)
                    delay = policy.MaxDelay;
                delivery.State = "pendiente";
                delivery.NextAttemptUtc = nowUtc + delay;
                update = AlertChannelUpdate.Scheduled;
            }
        }

        incident.PendingNotify = incident.Deliveries.Any(item => item.State == "pendiente");
        if (!incident.PendingNotify)
            incident.LastNotifiedUtc = nowUtc;
        if (incident.RecoveryPending && incident.Deliveries.All(item => item.State is "enviado" or "abandonado"))
            _incidents.Remove(key);
        _store.Save(_incidents.Values);
        return update;
    }

    private static void BeginCycle(AlertIncident incident, AlertDeliveryPolicy? policy)
    {
        incident.NoticeId = Guid.NewGuid().ToString("N");
        incident.Deliveries = CreateDeliveries(incident.NoticeId, policy);
        incident.PendingNotify = true;
    }

    private static List<AlertDeliveryState> CreateDeliveries(string noticeId, AlertDeliveryPolicy? policy)
    {
        var deliveries = new List<AlertDeliveryState>
        {
            new() { Channel = AlertChannels.Local, NoticeId = noticeId, State = "pendiente" }
        };
        if (policy?.WebhookEnabled == true)
        {
            deliveries.Add(new AlertDeliveryState
            {
                Channel = AlertChannels.Webhook,
                NoticeId = noticeId,
                State = "pendiente"
            });
        }

        return deliveries;
    }

    private static List<string> DueChannels(AlertIncident incident, DateTimeOffset nowUtc)
        => incident.Deliveries
            .Where(item => item.State == "pendiente" && (item.NextAttemptUtc is null || item.NextAttemptUtc <= nowUtc))
            .Select(item => item.Channel)
            .ToList();

    private static AlertNotice ToNotice(AlertIncident incident, string status, DateTimeOffset nowUtc)
        => new(incident.Key, status, incident.Severity, incident.Summary, incident.OpenedUtc, nowUtc, incident.NoticeId, DueChannels(incident, nowUtc));
}
