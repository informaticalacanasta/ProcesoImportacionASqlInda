namespace DbInda.Worker.Configuration;

public sealed class XsdValidationOptions
{
    public const string SectionName = "XsdValidation";

    public string SchemaFileName { get; set; } = "tiquets.xsd";
}
