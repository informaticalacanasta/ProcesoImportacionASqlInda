using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class SqlOptionsValidator : IValidateOptions<SqlOptions>
{
    public ValidateOptionsResult Validate(string? name, SqlOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DbInda))
            return ValidateOptionsResult.Fail("Falta ConnectionStrings:DbInda.");

        return ValidateOptionsResult.Success;
    }
}
