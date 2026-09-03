namespace DbInda.Worker.Models;

public sealed class ParseResult
{
    public bool Success { get; init; }
    public ParsedTicket? Ticket { get; init; }
    public ParsedFileName? FileName { get; init; }
    public IReadOnlyList<ConversionWarning> Warnings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<UnknownXmlElement> UnknownElements { get; init; } = [];
}
