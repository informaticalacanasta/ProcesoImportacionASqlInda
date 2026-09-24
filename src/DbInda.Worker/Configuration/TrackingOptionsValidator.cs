using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class TrackingOptionsValidator : IValidateOptions<TrackingOptions>
{
    public ValidateOptionsResult Validate(string? name, TrackingOptions options)
    {
        var errors = new List<string>();
        if (options.HeartbeatSeconds < 1)
            errors.Add("Tracking:HeartbeatSeconds debe ser mayor o igual que 1.");
        if (options.StaleSignalMinutes < 1)
            errors.Add("Tracking:StaleSignalMinutes debe ser mayor o igual que 1.");
        if (options.SqlDownMinutes < 1)
            errors.Add("Tracking:SqlDownMinutes debe ser mayor o igual que 1.");
        if (options.PendingWithoutProgressMinutes < 1)
            errors.Add("Tracking:PendingWithoutProgressMinutes debe ser mayor o igual que 1.");
        if (options.PendingMaxAgeMinutes < 1)
            errors.Add("Tracking:PendingMaxAgeMinutes debe ser mayor o igual que 1.");
        if (options.ArchiveStuckMinutes < 1)
            errors.Add("Tracking:ArchiveStuckMinutes debe ser mayor o igual que 1.");
        if (options.OutboxMaxAgeMinutes < 1)
            errors.Add("Tracking:OutboxMaxAgeMinutes debe ser mayor o igual que 1.");
        if (options.OutboxMaxFiles < 1)
            errors.Add("Tracking:OutboxMaxFiles debe ser mayor o igual que 1.");
        if (options.OutboxMaxBytes < 1)
            errors.Add("Tracking:OutboxMaxBytes debe ser mayor o igual que 1.");
        if (options.ReminderMinutes < 1)
            errors.Add("Tracking:ReminderMinutes debe ser mayor o igual que 1.");
        if (!BusinessTimeZoneResolver.TryResolve(options.BusinessTimeZone, out _))
            errors.Add("Tracking:BusinessTimeZone no es una zona horaria reconocida por el runtime.");
        if (options.Retention.Enabled && options.Retention.HistoryDays < 1)
            errors.Add("Tracking:Retention:HistoryDays debe ser mayor o igual que 1 cuando la retención está activa.");
        if (options.Retention.BatchSize < 1)
            errors.Add("Tracking:Retention:BatchSize debe ser mayor o igual que 1.");
        if (options.Alerts.RetrySeconds is < 1 or > 3600)
            errors.Add("Tracking:Alerts:RetrySeconds debe estar entre 1 y 3600.");
        if (options.Alerts.MaxAttempts is < 1 or > 20)
            errors.Add("Tracking:Alerts:MaxAttempts debe estar entre 1 y 20.");
        if (options.Alerts.WebhookEnabled && string.IsNullOrWhiteSpace(options.Alerts.WebhookUrl))
            errors.Add("Tracking:Alerts:WebhookUrl es obligatorio si Tracking:Alerts:WebhookEnabled es true.");
        if (options.Alerts.WebhookEnabled
            && !string.IsNullOrWhiteSpace(options.Alerts.WebhookUrl)
            && !Uri.TryCreate(options.Alerts.WebhookUrl, UriKind.Absolute, out var uri))
            errors.Add("Tracking:Alerts:WebhookUrl debe ser una URL absoluta.");
        else if (options.Alerts.WebhookEnabled
                 && Uri.TryCreate(options.Alerts.WebhookUrl, UriKind.Absolute, out var webhook)
                 && webhook.Scheme is not ("http" or "https"))
            errors.Add("Tracking:Alerts:WebhookUrl debe usar http o https.");

        for (var i = 0; i < options.ExpectedSources.Count; i++)
        {
            var source = options.ExpectedSources[i];
            if (source.ActiveDays.Count == 0)
                errors.Add($"Tracking:ExpectedSources[{i}] requiere ActiveDays. Sin horario no se alerta por tienda.");
            if (source.MaxSilenceMinutes < 1)
                errors.Add($"Tracking:ExpectedSources[{i}]:MaxSilenceMinutes debe ser mayor o igual que 1.");
            if (source.GraceMinutes < 0)
                errors.Add($"Tracking:ExpectedSources[{i}]:GraceMinutes no puede ser negativo.");
            if (!TimeOnly.TryParse(source.Start, out var start) || !TimeOnly.TryParse(source.End, out var end) || end <= start)
                errors.Add($"Tracking:ExpectedSources[{i}] requiere Start y End (HH:mm) con End posterior a Start.");
            if (source.ActiveDays.Any(day => !Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out _)))
                errors.Add($"Tracking:ExpectedSources[{i}]:ActiveDays usa nombres de DayOfWeek (Monday, Tuesday, …).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public static class BusinessTimeZoneResolver
{
    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) && found is not null)
        {
            zone = found;
            return true;
        }

        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows)
                && TimeZoneInfo.TryFindSystemTimeZoneById(windows, out found)
                && found is not null)
            {
                zone = found;
                return true;
            }
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        return false;
    }

    public static TimeZoneInfo Resolve(string id)
        => TryResolve(id, out var zone)
            ? zone
            : throw new InvalidOperationException($"Zona horaria no reconocida: {id}");
}
