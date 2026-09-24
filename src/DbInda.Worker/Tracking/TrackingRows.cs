using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbInda.Worker.Tracking;

public sealed class ExecutionRow
{
    public Guid IdEjecucion { get; set; }
    public long Secuencia { get; set; }
    public string NombreAplicacion { get; set; } = "DbInda.Worker";
    public string NombreInstancia { get; set; } = "";
    public string Equipo { get; set; } = "";
    public int IdProceso { get; set; }
    public string VersionAplicacion { get; set; } = "";
    public DateTime FechaInicioUtc { get; set; }
    public DateTime FechaSenalUtc { get; set; }
    public DateTime? FechaParadaUtc { get; set; }
    public string Estado { get; set; } = ExecutionStates.Activa;
    public string? MotivoFinalizacion { get; set; }
}

public sealed class AttemptRow
{
    public Guid IdIntento { get; set; }
    public long Secuencia { get; set; }
    public Guid IdEjecucion { get; set; }
    public long? IdRecepcion { get; set; }
    public long? IdTicket { get; set; }
    public string NombreFichero { get; set; } = "";
    public string RutaOrigen { get; set; } = "";
    public string CorrelacionOrigen { get; set; } = "";
    public string? HashSha256 { get; set; }
    public int? Tienda { get; set; }
    public int? Tpv { get; set; }
    public int? NumeroIntento { get; set; }
    public string TipoIntento { get; set; } = TrackingAttemptKinds.Conexion;
    public DateTime FechaInicioUtc { get; set; }
    public DateTime? FechaFinUtc { get; set; }
    public int? DuracionMs { get; set; }
    public string Fase { get; set; } = TrackingPhases.Recepcion;
    public string Resultado { get; set; } = TrackingResults.EnCurso;
    public string? CategoriaError { get; set; }
    public string? CodigoError { get; set; }
    public string? Mensaje { get; set; }
    public Guid? IdDetalleTecnico { get; set; }
    public DateTime? FechaProximoReintentoUtc { get; set; }
    public int? RegistrosConfirmados { get; set; }
}

public sealed class EventRow
{
    public Guid IdEvento { get; set; }
    public long Secuencia { get; set; }
    public DateTime FechaUtc { get; set; }
    public string Severidad { get; set; } = TrackingSeverity.Information;
    public string Tipo { get; set; } = "";
    public Guid? IdEjecucion { get; set; }
    public Guid? IdIntento { get; set; }
    public long? IdRecepcion { get; set; }
    public long? IdTicket { get; set; }
    public string? RutaOrigen { get; set; }
    public string? Fase { get; set; }
    public string? Detalle { get; set; }
}

public sealed class AttemptOutcome
{
    public required string Resultado { get; init; }
    public required string Fase { get; init; }
    public long? IdRecepcion { get; init; }
    public long? IdTicket { get; init; }
    public int? NumeroIntento { get; init; }
    public string? CategoriaError { get; init; }
    public string? CodigoError { get; init; }
    public string? Mensaje { get; init; }
    public string? DetalleTecnico { get; init; }
    public DateTimeOffset? ProximoReintentoUtc { get; init; }
    public int? RegistrosConfirmados { get; init; }
    public int? Tienda { get; init; }
    public int? Tpv { get; init; }
    public string? Hash { get; init; }
    public string? EventType { get; init; }
    public string Severidad { get; init; } = TrackingSeverity.Information;
}

public readonly record struct AttemptSlot(
    Guid Id,
    DateTimeOffset StartedUtc,
    long StartedTimestamp,
    string Path,
    string FileName,
    string? Hash,
    int? Tienda,
    int? Tpv);

public sealed class OutboxEnvelope
{
    public string Kind { get; set; } = "";
    public long Sequence { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Guid Id { get; set; }
    public ExecutionRow? Execution { get; set; }
    public AttemptRow? Attempt { get; set; }
    public EventRow? Event { get; set; }

    [JsonIgnore]
    public string? SourcePath { get; set; }
}

public readonly record struct OutboxMetrics(int Pending, DateTimeOffset? OldestUtc, bool Saturated, long PendingBytes);

public interface ITrackingStore
{
    Task UpsertExecutionAsync(ExecutionRow row, CancellationToken cancellationToken);
    Task UpsertAttemptAsync(AttemptRow row, CancellationToken cancellationToken);
    Task<bool> TryUpsertAttemptInTransactionAsync(
        Microsoft.Data.SqlClient.SqlConnection connection,
        System.Data.IDbTransaction transaction,
        AttemptRow row,
        EventRow? evento,
        CancellationToken cancellationToken);
    Task InsertEventAsync(EventRow row, CancellationToken cancellationToken);
    Task<bool> ProbeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AttemptRow>> ListOpenAttemptsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AttemptRow>> ListRecoverableAttemptsAsync(Guid currentExecutionId, DateTime staleSignalBeforeUtc, CancellationToken cancellationToken);
    Task<bool> TryReconcileOpenAttemptAsync(Guid attemptId, Guid originExecutionId, DateTime staleSignalBeforeUtc, string resultado, string mensaje, CancellationToken cancellationToken);
    Task<ReceptionEvidence?> ReadReceptionEvidenceAsync(long idRecepcion, CancellationToken cancellationToken);
    Task<bool> ScheduleRetryAsync(Guid attemptId, DateTime nextRetryUtc, CancellationToken cancellationToken);
    Task<string?> ReadReceptionEstadoAsync(long idRecepcion, CancellationToken cancellationToken);
    Task<OperationalReadout> ReadOperationalAsync(DateTime archiveStuckBeforeLocal, DateTime conflictsSinceLocal, CancellationToken cancellationToken);
    Task<int> DeleteBatchAsync(string sql, DateTime cutoffUtc, int batch, CancellationToken cancellationToken);
}

public sealed class ReceptionEvidence
{
    public string Estado { get; set; } = "";
    public int NumeroIntento { get; set; }
}

public sealed class OperationalReadout
{
    public bool QueriesSucceeded { get; init; }
    public int ArchivesStuck { get; init; }
    public int NewConflicts { get; init; }
    public IReadOnlyList<SourceImportMark> Sources { get; init; } = [];
}

public readonly record struct SourceImportMark(int? Tienda, int? Tpv, DateTime? FechaProcesadoLocal, DateTime? FinIntentoUtc);
