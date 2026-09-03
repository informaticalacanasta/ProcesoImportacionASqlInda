using DbInda.Worker.Models;

namespace DbInda.Worker.Processing;

public sealed class ImportResult
{
    public required string Status { get; init; }
    public long? ReceptionId { get; init; }
    public long? TicketId { get; init; }
    public IReadOnlyList<ConversionWarning> Warnings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool ReusedReception { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public bool ArchiveOnly { get; init; }
    public string EstadoArchivo { get; init; } = ArchiveStatuses.Pendiente;
    public bool SqlUnavailable { get; init; }
}
