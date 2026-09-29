using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class InboxCleanupOptionsValidator : IValidateOptions<InboxCleanupOptions>
{
    public ValidateOptionsResult Validate(string? name, InboxCleanupOptions options)
    {
        var errors = new List<string>();
        if (options.RetentionHours < 1)
            errors.Add("InboxCleanup:RetentionHours debe ser mayor o igual que 1.");
        if (options.ScanIntervalMinutes < 1)
            errors.Add("InboxCleanup:ScanIntervalMinutes debe ser mayor o igual que 1.");
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
