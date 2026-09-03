using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class PathsOptionsValidator : IValidateOptions<PathsOptions>
{
    public ValidateOptionsResult Validate(string? name, PathsOptions options)
    {
        var errors = new List<string>();
        Require(options.Input, "Paths:Input", errors);
        Require(options.Processed, "Paths:Processed", errors);
        Require(options.Errors, "Paths:Errors", errors);
        Require(options.Xsd, "Paths:Xsd", errors);
        Require(options.Logs, "Paths:Logs", errors);
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void Require(string value, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"Falta la configuración obligatoria '{key}'.");
    }
}
