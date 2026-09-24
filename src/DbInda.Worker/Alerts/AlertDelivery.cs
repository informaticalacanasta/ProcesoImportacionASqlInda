using System.Text.Json;
using DbInda.Worker.Configuration;
using DbInda.Worker.Tracking;

namespace DbInda.Worker.Alerts;

public interface IAlertSink
{
    Task<bool> PublishAsync(AlertNotice notice, CancellationToken cancellationToken);
}

public sealed class LocalAlertSink : IAlertSink
{
    private readonly string _path;
    private readonly ILogger<LocalAlertSink> _logger;
    private readonly object _sync = new();

    public LocalAlertSink(string path, ILogger<LocalAlertSink> logger)
    {
        _path = path;
        _logger = logger;
    }

    public Task<bool> PublishAsync(AlertNotice notice, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(notice);
        _logger.LogWarning("Alerta {Status} {Key}: {Summary}", notice.Status, notice.Key, notice.Summary);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            lock (_sync)
                File.AppendAllText(_path, line + Environment.NewLine);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "No se pudo escribir la alerta local {Key}. El aviso queda pendiente.", notice.Key);
            return Task.FromResult(false);
        }
    }
}

public sealed class WebhookAlertSink : IAlertSink
{
    private readonly TrackingOptions _options;
    private readonly HttpMessageHandler? _handler;
    private readonly ILogger<WebhookAlertSink> _logger;

    public WebhookAlertSink(Microsoft.Extensions.Options.IOptions<TrackingOptions> options, ILogger<WebhookAlertSink> logger)
        : this(options, logger, null)
    {
    }

    public WebhookAlertSink(Microsoft.Extensions.Options.IOptions<TrackingOptions> options, ILogger<WebhookAlertSink> logger, HttpMessageHandler? handler)
    {
        _options = options.Value;
        _logger = logger;
        _handler = handler;
    }

    public async Task<bool> PublishAsync(AlertNotice notice, CancellationToken cancellationToken)
    {
        if (!_options.Alerts.WebhookEnabled || string.IsNullOrWhiteSpace(_options.Alerts.WebhookUrl))
            return true;

        try
        {
            using var client = _handler is null
                ? new HttpClient()
                : new HttpClient(_handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var content = new StringContent(JsonSerializer.Serialize(notice), System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(_options.Alerts.WebhookUrl, content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            _logger.LogWarning("El webhook de alertas respondió {StatusCode} para {Key}.", (int)response.StatusCode, notice.Key);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("El webhook de alertas no completó el envío de {Key}.", notice.Key);
            return false;
        }
    }
}

public static class AlertFanOut
{
    public static async Task DeliverAsync(
        IReadOnlyList<AlertNotice> notices,
        IAlertSink local,
        IAlertSink webhook,
        AlertEngine engine,
        AlertDeliveryPolicy policy,
        DateTimeOffset nowUtc,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var notice in notices)
        {
            foreach (var channel in notice.ChannelsDue ?? [])
            {
                bool delivered;
                try
                {
                    delivered = channel == AlertChannels.Webhook
                        ? await webhook.PublishAsync(notice, cancellationToken).ConfigureAwait(false)
                        : await local.PublishAsync(notice, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    delivered = false;
                    logger.LogWarning("Fallo al entregar {Key} por {Channel} ({ExceptionType}).", notice.Key, channel, ex.GetType().Name);
                }

                var update = engine.MarkChannel(notice.Key, notice.NoticeId, channel, delivered, nowUtc, policy);
                if (update == AlertChannelUpdate.Abandoned)
                {
                    logger.LogWarning(
                        "Aviso {NoticeId} de {Key} abandonado en {Channel} tras {Attempts} intentos.",
                        notice.NoticeId,
                        notice.Key,
                        channel,
                        policy.MaxAttempts);
                }
            }
        }
    }
}

public static class AlertRules
{
    public static IReadOnlyList<RuleResult> Evaluate(
        HealthSnapshot health,
        TrackingOptions options,
        bool archivesEvaluated,
        int archivesStuck,
        bool conflictsEvaluated,
        int newConflicts,
        IReadOnlyList<(ExpectedSourceOptions Source, bool Due)> sources,
        DateTimeOffset nowUtc)
    {
        var rules = new List<RuleResult>
        {
            Signal(
                "sql-inaccesible",
                health.SqlDownSustained,
                "Error",
                $"SQL inaccesible desde {health.SqlUnreachableSinceUtc:O}."),
            Signal(
                "pendientes-sin-progreso",
                health.PendingStalled,
                "Warning",
                $"Hay {health.PendingOnDisk} XML en entrada sin progreso desde {health.OldestPendingUtc:O}. No sumar este dato a la cola: la cola ({health.QueueCount}) y el procesamiento ({health.InFlightCount}) ya están dentro de esos ficheros."),
            Signal(
                "pendientes-antiguos",
                health.OldestPendingUtc is not null && nowUtc - health.OldestPendingUtc >= TimeSpan.FromMinutes(options.PendingMaxAgeMinutes),
                "Warning",
                $"El XML pendiente más antiguo se observó en {health.OldestPendingUtc:O}."),
            Signal(
                "seguimiento-sin-enviar",
                health.OldestOutboxUtc is not null && nowUtc - health.OldestOutboxUtc >= TimeSpan.FromMinutes(options.OutboxMaxAgeMinutes),
                "Warning",
                $"Hay {health.OutboxPending} eventos de seguimiento locales. El más antiguo es de {health.OldestOutboxUtc:O}."),
            Signal(
                "seguimiento-lleno",
                health.OutboxSaturated,
                "Error",
                "La bandeja local de seguimiento ha alcanzado su límite o el disco no acepta más escrituras.")
        };

        if (archivesEvaluated)
        {
            rules.Add(Signal(
                "archivado-atascado",
                archivesStuck > 0,
                "Warning",
                $"{archivesStuck} recepciones siguen en ARCHIVANDO por encima del umbral."));
        }

        if (conflictsEvaluated)
        {
            rules.Add(Signal(
                "conflictos-nuevos",
                newConflicts > 0,
                "Warning",
                $"{newConflicts} conflictos de factura nuevos desde el arranque de esta ejecución."));
        }

        foreach (var (source, due) in sources)
        {
            rules.Add(Signal(
                $"silencio-{source.Tienda}-{source.Tpv}",
                due,
                "Warning",
                $"La tienda {source.Tienda} TPV {source.Tpv} no tiene una importación correcta dentro de su horario."));
        }

        return rules;
    }

    private static RuleResult Signal(string key, bool active, string severity, string summary)
        => new(key, true, active ? new AlertSignal(key, severity, summary) : null);
}
