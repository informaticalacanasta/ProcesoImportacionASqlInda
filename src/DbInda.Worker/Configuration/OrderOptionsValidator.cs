using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class OrderOptionsValidator : IValidateOptions<OrderOptions>
{
    public ValidateOptionsResult Validate(string? name, OrderOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Pending))
            errors.Add("Orders:Pending es obligatorio.");
        if (string.IsNullOrWhiteSpace(options.Processed))
            errors.Add("Orders:Processed es obligatorio.");
        if (string.IsNullOrWhiteSpace(options.Errors))
            errors.Add("Orders:Errors es obligatorio.");
        if (options.MaxConcurrency < 1)
            errors.Add("Orders:MaxConcurrency debe ser mayor o igual que 1.");
        if (options.ScanIntervalSeconds < 1)
            errors.Add("Orders:ScanIntervalSeconds debe ser mayor o igual que 1.");

        if (errors.Count == 0)
        {
            var pending = Path.GetFullPath(options.Pending);
            var processed = Path.GetFullPath(options.Processed);
            var orderErrors = Path.GetFullPath(options.Errors);
            if (Same(pending, processed) || Same(pending, orderErrors) || Same(processed, orderErrors))
                errors.Add("Orders:Pending, Processed y Errors deben ser carpetas distintas.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool Same(string left, string right)
        => string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
