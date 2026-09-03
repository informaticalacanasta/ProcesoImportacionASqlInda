using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class RetryOptionsValidator : IValidateOptions<RetryOptions>
{
    public ValidateOptionsResult Validate(string? name, RetryOptions options)
    {
        var errors = new List<string>();
        if (options.InitialDelaySeconds < 1)
            errors.Add("Retry:InitialDelaySeconds debe ser mayor o igual que 1.");
        if (options.MaxDelaySeconds < options.InitialDelaySeconds)
            errors.Add("Retry:MaxDelaySeconds debe ser mayor o igual que Retry:InitialDelaySeconds.");
        if (options.BackoffMultiplier < 1)
            errors.Add("Retry:BackoffMultiplier debe ser mayor o igual que 1.");
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
