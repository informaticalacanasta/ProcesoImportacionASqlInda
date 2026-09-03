using DbInda.Worker.Models;

namespace DbInda.Worker.Persistence;

public sealed class ReceptionLookup
{
    public long IdRecepcion { get; init; }
    public string Estado { get; init; } = "";
    public string EstadoArchivo { get; init; } = ArchiveStatuses.Pendiente;
    public int NumeroIntento { get; init; }
    public string? RutaOrigen { get; init; }
    public string? RutaFinal { get; init; }
    public string? RutaDestinoPrevista { get; init; }
    public string? HashSha256 { get; init; }
    public long? IdTicket { get; init; }
    public DateTime? FechaPrimerIntento { get; init; }
}

public sealed class IncompleteArchiveRow
{
    public long IdRecepcion { get; init; }
    public string Estado { get; init; } = "";
    public string EstadoArchivo { get; init; } = ArchiveStatuses.Pendiente;
    public string RutaOrigen { get; init; } = "";
    public string? RutaDestinoPrevista { get; init; }
    public string? HashSha256 { get; init; }
    public string NombreFichero { get; init; } = "";
    public DateTime? FechaFichero { get; init; }
    public int? TiendaFichero { get; init; }
    public DateTime? FechaProcesado { get; init; }
    public DateTime FechaRecepcion { get; init; }
}
