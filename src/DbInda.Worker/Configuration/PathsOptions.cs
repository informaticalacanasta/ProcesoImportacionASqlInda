namespace DbInda.Worker.Configuration;

public sealed class PathsOptions
{
    public const string SectionName = "Paths";

    public string Input { get; set; } = "";
    public string Processed { get; set; } = "";
    public string Errors { get; set; } = "";
    public string Xsd { get; set; } = "";
    public string Logs { get; set; } = "";
}
