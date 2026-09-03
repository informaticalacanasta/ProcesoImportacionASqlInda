using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class ProcessingOptionsValidator : IValidateOptions<ProcessingOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessingOptions options)
    {
        var errors = new List<string>();
        if (options.MaxConcurrency < 1)
            errors.Add("Processing:MaxConcurrency debe ser mayor o igual que 1.");
        if (options.QueueCapacity < 1)
            errors.Add("Processing:QueueCapacity debe ser mayor o igual que 1.");
        if (options.ScanIntervalSeconds < 1)
            errors.Add("Processing:ScanIntervalSeconds debe ser mayor o igual que 1.");
        if (options.StableChecks < 1)
            errors.Add("Processing:StableChecks debe ser mayor o igual que 1.");
        if (options.StableCheckDelayMilliseconds < 1)
            errors.Add("Processing:StableCheckDelayMilliseconds debe ser mayor o igual que 1.");
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
