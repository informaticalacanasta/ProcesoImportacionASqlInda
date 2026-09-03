namespace DbInda.Worker.Configuration;

public sealed class SqlOptions
{
    public const string SectionName = "ConnectionStrings";

    public string DbInda { get; set; } = "";
}
