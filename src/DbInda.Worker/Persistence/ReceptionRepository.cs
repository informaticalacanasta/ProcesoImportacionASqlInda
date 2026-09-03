using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using DbInda.Worker.Models;

namespace DbInda.Worker.Persistence;

public sealed class ReceptionInsert
{
    public required DateTime FechaRecepcion { get; init; }
    public required string NombreFichero { get; init; }
    public required string RutaOrigen { get; init; }
    public string? RutaFinal { get; init; }
    public string? HashSha256 { get; init; }
    public long? TamanoBytes { get; init; }
    public required string Estado { get; init; }
    public int NumeroIntento { get; init; } = 1;
    public DateTime? FechaPrimerIntento { get; init; }
    public DateTime? FechaUltimoIntento { get; init; }
    public DateTime? FechaProcesado { get; init; }
    public long? IdTicket { get; init; }
    public bool EsDuplicado { get; init; }
    public bool EsConflictoMismaFactura { get; init; }
    public long? IdRecepcionOriginal { get; init; }
    public string? HashTicketAsociado { get; init; }
    public bool? XsdValido { get; init; }
    public string? EstadoValidacionXsd { get; init; }
    public int NumeroWarnings { get; init; }
    public int NumeroErrores { get; init; }
    public string? MensajeError { get; init; }
    public string? DetalleAdvertencias { get; init; }
    public string? DetalleXsd { get; init; }
    public string? NombreNifFichero { get; init; }
    public string? SerieFichero { get; init; }
    public int? TiendaFichero { get; init; }
    public int? TpvFichero { get; init; }
    public string? NumFacturaFichero { get; init; }
    public DateOnly? FechaFichero { get; init; }
    public TimeOnly? HoraFichero { get; init; }
    public decimal? ImporteFichero { get; init; }
}

public sealed class ReceptionUpdate
{
    public required long IdRecepcion { get; init; }
    public required string Estado { get; init; }
    public DateTime FechaUltimoIntento { get; init; }
    public DateTime? FechaProcesado { get; init; }
    public long? IdTicket { get; init; }
    public bool EsDuplicado { get; init; }
    public bool EsConflictoMismaFactura { get; init; }
    public long? IdRecepcionOriginal { get; init; }
    public string? HashTicketAsociado { get; init; }
    public int NumeroWarnings { get; init; }
    public int NumeroErrores { get; init; }
    public string? MensajeError { get; init; }
    public string? DetalleAdvertencias { get; init; }
}

public sealed class ReceptionRepository
{
    public async Task<long> InsertAsync(SqlConnection connection, IDbTransaction? transaction, ReceptionInsert row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.TICKET_RECEPCION (
                FECHA_RECEPCION, NOMBRE_FICHERO, RUTA_ORIGEN, RUTA_FINAL, HASH_SHA256, TAMANO_BYTES,
                ESTADO, NUMERO_INTENTO, FECHA_PRIMER_INTENTO, FECHA_ULTIMO_INTENTO, FECHA_PROCESADO,
                ID_TICKET, ES_DUPLICADO, ES_CONFLICTO_MISMA_FACTURA, ID_RECEPCION_ORIGINAL, HASH_TICKET_ASOCIADO,
                XSD_VALIDO, ESTADO_VALIDACION_XSD, NUMERO_WARNINGS, NUMERO_ERRORES, MENSAJE_ERROR,
                DETALLE_ADVERTENCIAS, DETALLE_XSD,
                NOMBRE_NIF_FICHERO, SERIE_FICHERO, TIENDA_FICHERO, TPV_FICHERO, NUM_FACTURA_FICHERO,
                FECHA_FICHERO, HORA_FICHERO, IMPORTE_FICHERO)
            OUTPUT INSERTED.ID_RECEPCION
            VALUES (
                @FechaRecepcion, @NombreFichero, @RutaOrigen, @RutaFinal, @HashSha256, @TamanoBytes,
                @Estado, @NumeroIntento, @FechaPrimerIntento, @FechaUltimoIntento, @FechaProcesado,
                @IdTicket, @EsDuplicado, @EsConflictoMismaFactura, @IdRecepcionOriginal, @HashTicketAsociado,
                @XsdValido, @EstadoValidacionXsd, @NumeroWarnings, @NumeroErrores, @MensajeError,
                @DetalleAdvertencias, @DetalleXsd,
                @NombreNifFichero, @SerieFichero, @TiendaFichero, @TpvFichero, @NumFacturaFichero,
                @FechaFichero, @HoraFichero, @ImporteFichero);
            """;

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                row.FechaRecepcion,
                row.NombreFichero,
                row.RutaOrigen,
                row.RutaFinal,
                row.HashSha256,
                row.TamanoBytes,
                row.Estado,
                row.NumeroIntento,
                row.FechaPrimerIntento,
                row.FechaUltimoIntento,
                row.FechaProcesado,
                row.IdTicket,
                row.EsDuplicado,
                row.EsConflictoMismaFactura,
                row.IdRecepcionOriginal,
                row.HashTicketAsociado,
                row.XsdValido,
                row.EstadoValidacionXsd,
                row.NumeroWarnings,
                row.NumeroErrores,
                row.MensajeError,
                row.DetalleAdvertencias,
                row.DetalleXsd,
                row.NombreNifFichero,
                row.SerieFichero,
                row.TiendaFichero,
                row.TpvFichero,
                row.NumFacturaFichero,
                FechaFichero = SqlTemporal.ToDbDate(row.FechaFichero),
                HoraFichero = SqlTemporal.ToDbTime(row.HoraFichero),
                row.ImporteFichero
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(SqlConnection connection, IDbTransaction? transaction, ReceptionUpdate row, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.TICKET_RECEPCION
            SET ESTADO = @Estado,
                FECHA_ULTIMO_INTENTO = @FechaUltimoIntento,
                FECHA_PROCESADO = @FechaProcesado,
                ID_TICKET = @IdTicket,
                ES_DUPLICADO = @EsDuplicado,
                ES_CONFLICTO_MISMA_FACTURA = @EsConflictoMismaFactura,
                ID_RECEPCION_ORIGINAL = @IdRecepcionOriginal,
                HASH_TICKET_ASOCIADO = @HashTicketAsociado,
                NUMERO_WARNINGS = @NumeroWarnings,
                NUMERO_ERRORES = @NumeroErrores,
                MENSAJE_ERROR = @MensajeError,
                DETALLE_ADVERTENCIAS = @DetalleAdvertencias
            WHERE ID_RECEPCION = @IdRecepcion;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, row, transaction, cancellationToken: cancellationToken));
    }

    public async Task<ReceptionLookup?> FindByOriginAndHashAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        string rutaOrigen,
        string hashSha256,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                ID_RECEPCION AS IdRecepcion,
                ESTADO AS Estado,
                ESTADO_ARCHIVO AS EstadoArchivo,
                NUMERO_INTENTO AS NumeroIntento,
                RUTA_ORIGEN AS RutaOrigen,
                RUTA_FINAL AS RutaFinal,
                RUTA_DESTINO_PREVISTA AS RutaDestinoPrevista,
                HASH_SHA256 AS HashSha256,
                ID_TICKET AS IdTicket,
                FECHA_PRIMER_INTENTO AS FechaPrimerIntento
            FROM dbo.TICKET_RECEPCION
            WHERE RUTA_ORIGEN = @RutaOrigen
              AND HASH_SHA256 = @HashSha256
            ORDER BY ID_RECEPCION DESC;
            """;

        return await connection.QuerySingleOrDefaultAsync<ReceptionLookup>(
            new CommandDefinition(
                sql,
                new { RutaOrigen = rutaOrigen, HashSha256 = hashSha256 },
                transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<int> PrepareRetryAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        long idRecepcion,
        DateTime fechaUltimoIntento,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.TICKET_RECEPCION
            SET NUMERO_INTENTO = NUMERO_INTENTO + 1,
                FECHA_ULTIMO_INTENTO = @FechaUltimoIntento,
                ESTADO = @Estado,
                FECHA_PROCESADO = NULL,
                MENSAJE_ERROR = NULL
            OUTPUT INSERTED.NUMERO_INTENTO
            WHERE ID_RECEPCION = @IdRecepcion
              AND ESTADO IN (@ErrorSql, @Pendiente, @Procesando);
            """;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new
            {
                IdRecepcion = idRecepcion,
                FechaUltimoIntento = fechaUltimoIntento,
                Estado = ReceptionStatuses.Pendiente,
                ErrorSql = ReceptionStatuses.ErrorSql,
                Pendiente = ReceptionStatuses.Pendiente,
                Procesando = ReceptionStatuses.Procesando
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task MarkArchivingAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        long idRecepcion,
        string rutaDestinoPrevista,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.TICKET_RECEPCION
            SET ESTADO_ARCHIVO = @EstadoArchivo,
                RUTA_DESTINO_PREVISTA = @RutaDestinoPrevista
            WHERE ID_RECEPCION = @IdRecepcion
              AND RUTA_FINAL IS NULL
              AND ESTADO_ARCHIVO IN (@Pendiente, @Archivando);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                IdRecepcion = idRecepcion,
                RutaDestinoPrevista = rutaDestinoPrevista,
                EstadoArchivo = ArchiveStatuses.Archivando,
                Pendiente = ArchiveStatuses.Pendiente,
                Archivando = ArchiveStatuses.Archivando
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task MarkArchivedAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        long idRecepcion,
        string rutaFinal,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.TICKET_RECEPCION
            SET RUTA_FINAL = @RutaFinal,
                ESTADO_ARCHIVO = @EstadoArchivo,
                RUTA_DESTINO_PREVISTA = NULL
            WHERE ID_RECEPCION = @IdRecepcion
              AND ESTADO_ARCHIVO IN (@Pendiente, @Archivando);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                IdRecepcion = idRecepcion,
                RutaFinal = rutaFinal,
                EstadoArchivo = ArchiveStatuses.Archivado,
                Pendiente = ArchiveStatuses.Pendiente,
                Archivando = ArchiveStatuses.Archivando
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task MarkArchiveErrorAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        long idRecepcion,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.TICKET_RECEPCION
            SET ESTADO_ARCHIVO = @EstadoArchivo
            WHERE ID_RECEPCION = @IdRecepcion
              AND ESTADO_ARCHIVO IN (@Pendiente, @Archivando);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                IdRecepcion = idRecepcion,
                EstadoArchivo = ArchiveStatuses.ErrorArchivo,
                Pendiente = ArchiveStatuses.Pendiente,
                Archivando = ArchiveStatuses.Archivando
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<IncompleteArchiveRow>> ListIncompleteArchivesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ID_RECEPCION AS IdRecepcion,
                ESTADO AS Estado,
                ESTADO_ARCHIVO AS EstadoArchivo,
                RUTA_ORIGEN AS RutaOrigen,
                RUTA_DESTINO_PREVISTA AS RutaDestinoPrevista,
                HASH_SHA256 AS HashSha256,
                NOMBRE_FICHERO AS NombreFichero,
                FECHA_FICHERO AS FechaFichero,
                TIENDA_FICHERO AS TiendaFichero,
                FECHA_PROCESADO AS FechaProcesado,
                FECHA_RECEPCION AS FechaRecepcion
            FROM dbo.TICKET_RECEPCION
            WHERE ESTADO_ARCHIVO IN (@Pendiente, @Archivando)
              AND ESTADO IN (
                    @Procesado, @ProcesadoAdv, @Duplicado, @Conflicto,
                    @ErrorXml, @ErrorPermanente)
            ORDER BY ID_RECEPCION;
            """;

        var rows = await connection.QueryAsync<IncompleteArchiveRow>(new CommandDefinition(
            sql,
            new
            {
                Pendiente = ArchiveStatuses.Pendiente,
                Archivando = ArchiveStatuses.Archivando,
                Procesado = ReceptionStatuses.Procesado,
                ProcesadoAdv = ReceptionStatuses.ProcesadoConAdvertencias,
                Duplicado = ReceptionStatuses.Duplicado,
                Conflicto = ReceptionStatuses.ConflictoMismaFactura,
                ErrorXml = ReceptionStatuses.ErrorXml,
                ErrorPermanente = ReceptionStatuses.ErrorPermanente
            },
            cancellationToken: cancellationToken));

        return rows.AsList();
    }
}
