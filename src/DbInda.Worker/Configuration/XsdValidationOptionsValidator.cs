using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public sealed class XsdValidationOptionsValidator : IValidateOptions<XsdValidationOptions>
{
    public ValidateOptionsResult Validate(string? name, XsdValidationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SchemaFileName))
            return ValidateOptionsResult.Fail("Falta XsdValidation:SchemaFileName.");
        return ValidateOptionsResult.Success;
    }
}
