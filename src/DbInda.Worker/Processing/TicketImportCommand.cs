using DbInda.Worker.Models;

namespace DbInda.Worker.Processing;

public sealed class TicketImportCommand
{
    public required string FileName { get; init; }
    public required string OriginPath { get; init; }
    public required byte[] FileBytes { get; init; }
    public required ParseResult Parse { get; init; }
    public required XsdValidationResult Xsd { get; init; }
}
