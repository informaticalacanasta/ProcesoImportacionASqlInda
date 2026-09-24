using System.Diagnostics;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Models;
using DbInda.Worker.Persistence;
using DbInda.Worker.Tracking;
using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Processing;

public sealed class TicketImportProcessor
{
    private readonly SqlConnectionFactory _connections;
    private readonly ReceptionRepository _receptions;
    private readonly TicketRepository _tickets;
    private readonly ImportTracker? _tracker;

    public TicketImportProcessor(
        SqlConnectionFactory connections,
        ReceptionRepository receptions,
        TicketRepository tickets,
        ImportTracker? tracker = null)
    {
        _connections = connections;
        _receptions = receptions;
        _tickets = tickets;
        _tracker = tracker;
    }

    public async Task<ImportResult> ImportAsync(TicketImportCommand command, CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var started = Stopwatch.GetTimestamp();
        AttemptSlot? slot = null;
        var originPath = FilePathNormalizer.Normalize(command.OriginPath);
        var hash = command.FileBytes.Length > 0 ? Sha256FileHasher.ComputeHex(command.FileBytes) : null;
        var parseWarnings = command.Parse.Warnings.ToList();
        var mapped = command.Parse is { Success: true, Ticket: not null }
            ? TicketSqlMapper.Map(command.Parse.Ticket)
            : null;

        if (mapped is not null)
            parseWarnings.AddRange(mapped.Warnings);

        if (command.Xsd.EstadoValidacionXsd == XsdValidationStatuses.InvalidoDatos)
        {
            parseWarnings.Add(new ConversionWarning
            {
                Code = "XSD_INVALIDO_DATOS",
                Field = "XSD",
                Message = "El XSD reportó errores de datos ajenos a las incompatibilidades conocidas.",
                RawValue = command.Xsd.EstadoValidacionXsd
            });
        }

        var qualityWarnings = parseWarnings.Where(WarningText.AffectsTicketQuality).ToList();
        var fileName = command.Parse.FileName;
        long receptionId;
        var reused = false;
        var attemptNumber = 1;

        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken);

            ReceptionLookup? existing = null;
            if (hash is not null)
                existing = await _receptions.FindByOriginAndHashAsync(connection, null, originPath, hash, cancellationToken);

            if (existing is not null && ReceptionLifecycle.IsIncompleteArchive(existing.Estado, existing.EstadoArchivo))
            {
                await NoteArchiveRecoveryAsync(existing.IdRecepcion, originPath, cancellationToken).ConfigureAwait(false);
                return ArchiveOnly(existing, parseWarnings, started);
            }

            if (existing is not null && ReceptionLifecycle.IsRecoverable(existing.Estado) && existing.RutaFinal is null)
            {
                var nextAttempt = await _receptions.PrepareRetryAsync(connection, null, existing.IdRecepcion, now, cancellationToken);
                if (nextAttempt > 0)
                {
                    receptionId = existing.IdRecepcion;
                    reused = true;
                    attemptNumber = nextAttempt;
                }
                else
                {
                    existing = await _receptions.FindByOriginAndHashAsync(connection, null, originPath, hash!, cancellationToken);
                    if (existing is not null && ReceptionLifecycle.IsIncompleteArchive(existing.Estado, existing.EstadoArchivo))
                    {
                        await NoteArchiveRecoveryAsync(existing.IdRecepcion, originPath, cancellationToken).ConfigureAwait(false);
                        return ArchiveOnly(existing, parseWarnings, started);
                    }

                    receptionId = await _receptions.InsertAsync(
                        connection, null, BuildPendiente(command, originPath, fileName, hash, now, parseWarnings), cancellationToken);
                }
            }
            else
            {
                receptionId = await _receptions.InsertAsync(
                    connection, null, BuildPendiente(command, originPath, fileName, hash, now, parseWarnings), cancellationToken);
            }
        }
        catch (Exception ex) when (SqlAvailability.IsUnavailable(ex))
        {
            slot = await NoteConnectionFailureAsync(command, originPath, hash, fileName, ex, cancellationToken).ConfigureAwait(false);
            return new ImportResult
            {
                Status = ReceptionStatuses.ErrorSql,
                Warnings = parseWarnings,
                Errors = [ex.Message],
                SqlUnavailable = true,
                AttemptId = slot?.Id,
                DurationMs = Elapsed(started)
            };
        }

        slot = await NoteReceptionAttemptAsync(command, originPath, hash, fileName, receptionId, attemptNumber, cancellationToken).ConfigureAwait(false);

        if (!command.Parse.Success || command.Parse.Ticket is null || mapped is null)
        {
            await UpdateReceptionAsync(new ReceptionUpdate
            {
                IdRecepcion = receptionId,
                Estado = ReceptionStatuses.ErrorXml,
                FechaUltimoIntento = DateTime.Now,
                NumeroWarnings = parseWarnings.Count,
                NumeroErrores = Math.Max(1, command.Parse.Errors.Count),
                MensajeError = command.Parse.Errors.Count == 0 ? "El XML no pudo parsearse." : string.Join("; ", command.Parse.Errors),
                DetalleAdvertencias = WarningText.Join(parseWarnings)
            }, cancellationToken);

            var xmlMessage = command.Parse.Errors.Count == 0
                ? "El XML no pudo parsearse."
                : string.Join("; ", command.Parse.Errors);
            await TrackTerminalAsync(slot, OutcomeFor(
                ReceptionStatuses.ErrorXml, receptionId, null, attemptNumber, fileName, hash, xmlMessage, "XML", null), cancellationToken).ConfigureAwait(false);
            return Result(ReceptionStatuses.ErrorXml, receptionId, null, parseWarnings,
                command.Parse.Errors.Count == 0 ? ["El XML no pudo parsearse."] : command.Parse.Errors,
                reused, attemptNumber, slot, started);
        }

        if (hash is null)
        {
            await UpdateReceptionAsync(new ReceptionUpdate
            {
                IdRecepcion = receptionId,
                Estado = ReceptionStatuses.ErrorPermanente,
                FechaUltimoIntento = DateTime.Now,
                NumeroWarnings = parseWarnings.Count,
                NumeroErrores = 1,
                MensajeError = "No se pudo calcular HASH_SHA256 porque el fichero no tiene bytes.",
                DetalleAdvertencias = WarningText.Join(parseWarnings)
            }, cancellationToken);

            const string hashMessage = "No se pudo calcular HASH_SHA256 porque el fichero no tiene bytes.";
            await TrackTerminalAsync(slot, OutcomeFor(
                ReceptionStatuses.ErrorPermanente, receptionId, null, attemptNumber, fileName, hash, hashMessage, "XML", null), cancellationToken).ConfigureAwait(false);
            return Result(ReceptionStatuses.ErrorPermanente, receptionId, null, parseWarnings,
                [hashMessage], reused, attemptNumber, slot, started);
        }

        var commitStarted = false;
        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken);

            var duplicate = await _tickets.FindByHashAsync(connection, null, hash, cancellationToken);
            if (duplicate is not null)
                return await MarkDuplicateAsync(receptionId, duplicate, parseWarnings, reused, attemptNumber, slot, started, cancellationToken);

            var identity = mapped.NifEmisor is not null && mapped.NumFactura is not null && mapped.FechaExpedicion is not null
                ? await _tickets.FindByIdentityAsync(
                    connection, null, mapped.NifEmisor, mapped.SerieFactura, mapped.NumFactura, mapped.FechaExpedicion.Value,
                    mapped.Tienda, mapped.Tpv, cancellationToken)
                : null;
            if (identity is not null)
                return await MarkConflictAsync(receptionId, identity, parseWarnings, reused, attemptNumber, slot, started, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var quality = TicketQualityEvaluator.Evaluate(mapped.Source, qualityWarnings);
                var ticketId = await _tickets.InsertGraphAsync(
                    connection,
                    transaction,
                    mapped,
                    receptionId,
                    hash,
                    quality,
                    now,
                    qualityWarnings.Count,
                    cancellationToken);

                var receptionStatus = parseWarnings.Count > 0
                    ? ReceptionStatuses.ProcesadoConAdvertencias
                    : ReceptionStatuses.Procesado;

                await _receptions.UpdateAsync(
                    connection,
                    transaction,
                    new ReceptionUpdate
                    {
                        IdRecepcion = receptionId,
                        Estado = receptionStatus,
                        FechaUltimoIntento = DateTime.Now,
                        FechaProcesado = DateTime.Now,
                        IdTicket = ticketId,
                        NumeroWarnings = parseWarnings.Count,
                        NumeroErrores = 0,
                        DetalleAdvertencias = WarningText.Join(parseWarnings)
                    },
                    cancellationToken);

                var success = OutcomeFor(
                    receptionStatus, receptionId, ticketId, attemptNumber, fileName, hash, receptionStatus, null, null,
                    mapped.Tienda, mapped.Tpv, CountConfirmed(mapped));
                commitStarted = true;
                await transaction.CommitAsync(cancellationToken);
                // Fuera de la transacción: un error de seguimiento puede dejarla no confirmable en SQL Server.
                await TrackCommittedAsync(slot, success, cancellationToken).ConfigureAwait(false);

                return Result(receptionStatus, receptionId, ticketId, parseWarnings, [], reused, attemptNumber, slot, started);
            }
            catch (Exception ex) when (SqlUniqueConstraint.IsTicketHashDuplicate(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                var existingTicket = await _tickets.FindByHashAsync(connection, transaction: null, hash, cancellationToken);
                if (existingTicket is null)
                    throw;

                return await MarkDuplicateAsync(receptionId, existingTicket, parseWarnings, reused, attemptNumber, slot, started, cancellationToken);
            }
            catch (Exception ex)
            {
                if (commitStarted && SqlAvailability.IsUnavailable(ex))
                {
                    var resolved = await ResolveUncertainCommitAsync(
                        receptionId, ex, slot, parseWarnings, reused, attemptNumber, fileName, hash, started, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null)
                        return resolved;
                }

                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch (Exception rollbackEx) when (rollbackEx is not OperationCanceledException)
                {
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            try
            {
                await UpdateReceptionAsync(new ReceptionUpdate
                {
                    IdRecepcion = receptionId,
                    Estado = ReceptionStatuses.ErrorSql,
                    FechaUltimoIntento = DateTime.Now,
                    NumeroWarnings = parseWarnings.Count,
                    NumeroErrores = 1,
                    MensajeError = ex.Message,
                    DetalleAdvertencias = WarningText.Join(parseWarnings)
                }, cancellationToken);
            }
            catch (Exception updateEx) when (updateEx is not OperationCanceledException)
            {
            }

            await TrackTerminalAsync(slot, OutcomeFor(
                ReceptionStatuses.ErrorSql, receptionId, null, attemptNumber, fileName, hash, ex.Message, "SQL",
                SqlExceptionDetails.ToJson(ex)), cancellationToken).ConfigureAwait(false);
            return Result(ReceptionStatuses.ErrorSql, receptionId, null, parseWarnings, [ex.Message], reused, attemptNumber, slot, started);
        }
    }

    public async Task BeginArchiveAsync(long idRecepcion, string rutaDestinoPrevista, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await _receptions.MarkArchivingAsync(connection, null, idRecepcion, rutaDestinoPrevista, cancellationToken);
        if (_tracker is not null)
            await _tracker.RecordArchiveEventAsync(
                TrackingEventTypes.ArchivadoIniciado,
                TrackingSeverity.Information,
                idRecepcion,
                rutaDestinoPrevista,
                "Archivado iniciado.",
                cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteArchiveAsync(long idRecepcion, string rutaFinal, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await _receptions.MarkArchivedAsync(connection, null, idRecepcion, rutaFinal, cancellationToken);
        if (_tracker is not null)
            await _tracker.RecordArchiveEventAsync(
                TrackingEventTypes.ArchivadoCompletado,
                TrackingSeverity.Information,
                idRecepcion,
                rutaFinal,
                "Archivado completado.",
                cancellationToken).ConfigureAwait(false);
    }

    private ReceptionInsert BuildPendiente(
        TicketImportCommand command,
        string originPath,
        ParsedFileName? fileName,
        string? hash,
        DateTime now,
        IReadOnlyList<ConversionWarning> warnings)
    {
        string? serieFichero = null;
        if (fileName is { PatternMatched: true, Tienda: not null, Tpv: not null, NumFactura: not null })
            serieFichero = $"{fileName.Tienda}-{fileName.Tpv}-{fileName.NumFactura}";

        var nifFichero = fileName?.NifEmisor is { Length: 9 } nif ? nif : null;

        return new ReceptionInsert
        {
            FechaRecepcion = now,
            NombreFichero = Path.GetFileName(command.FileName),
            RutaOrigen = originPath,
            HashSha256 = hash,
            TamanoBytes = command.FileBytes.LongLength,
            Estado = ReceptionStatuses.Pendiente,
            NumeroIntento = 1,
            FechaPrimerIntento = now,
            FechaUltimoIntento = now,
            XsdValido = command.Xsd.XsdValido,
            EstadoValidacionXsd = command.Xsd.EstadoValidacionXsd,
            NumeroWarnings = warnings.Count,
            NumeroErrores = command.Parse.Errors.Count,
            MensajeError = command.Parse.Errors.Count == 0 ? null : string.Join("; ", command.Parse.Errors),
            DetalleAdvertencias = WarningText.Join(warnings),
            DetalleXsd = WarningText.JoinXsd(command.Xsd.Events),
            NombreNifFichero = nifFichero,
            SerieFichero = serieFichero,
            TiendaFichero = fileName?.Tienda,
            TpvFichero = fileName?.Tpv,
            NumFacturaFichero = fileName?.NumFactura is { Length: <= 20 } num ? num : null,
            FechaFichero = fileName?.Fecha,
            HoraFichero = fileName?.Hora,
            ImporteFichero = fileName?.Importe
        };
    }

    private async Task<ImportResult> MarkDuplicateAsync(
        long receptionId,
        ExistingTicketRef existing,
        List<ConversionWarning> warnings,
        bool reused,
        int attemptNumber,
        AttemptSlot? slot,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        await UpdateReceptionAsync(new ReceptionUpdate
        {
            IdRecepcion = receptionId,
            Estado = ReceptionStatuses.Duplicado,
            FechaUltimoIntento = DateTime.Now,
            FechaProcesado = DateTime.Now,
            IdTicket = existing.IdTicket,
            EsDuplicado = true,
            IdRecepcionOriginal = existing.IdRecepcionOrigen,
            HashTicketAsociado = existing.HashSha256,
            NumeroWarnings = warnings.Count,
            DetalleAdvertencias = WarningText.Join(warnings)
        }, cancellationToken);

        await TrackTerminalAsync(slot, new AttemptOutcome
        {
            Resultado = TrackingResults.Duplicado,
            Fase = TrackingPhases.Insercion,
            IdRecepcion = receptionId,
            IdTicket = existing.IdTicket,
            NumeroIntento = attemptNumber,
            Mensaje = "Duplicado de un ticket ya importado.",
            Severidad = TrackingSeverity.Information
        }, cancellationToken).ConfigureAwait(false);
        return Result(ReceptionStatuses.Duplicado, receptionId, existing.IdTicket, warnings, [], reused, attemptNumber, slot, startedTimestamp);
    }

    private async Task<ImportResult> MarkConflictAsync(
        long receptionId,
        ExistingTicketRef existing,
        List<ConversionWarning> warnings,
        bool reused,
        int attemptNumber,
        AttemptSlot? slot,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        await UpdateReceptionAsync(new ReceptionUpdate
        {
            IdRecepcion = receptionId,
            Estado = ReceptionStatuses.ConflictoMismaFactura,
            FechaUltimoIntento = DateTime.Now,
            FechaProcesado = DateTime.Now,
            IdTicket = existing.IdTicket,
            EsConflictoMismaFactura = true,
            IdRecepcionOriginal = existing.IdRecepcionOrigen,
            HashTicketAsociado = existing.HashSha256,
            NumeroWarnings = warnings.Count,
            DetalleAdvertencias = WarningText.Join(warnings)
        }, cancellationToken);

        await TrackTerminalAsync(slot, new AttemptOutcome
        {
            Resultado = TrackingResults.Conflicto,
            Fase = TrackingPhases.Insercion,
            IdRecepcion = receptionId,
            IdTicket = existing.IdTicket,
            NumeroIntento = attemptNumber,
            Mensaje = "Conflicto de la misma factura con otro hash.",
            Severidad = TrackingSeverity.Warning
        }, cancellationToken).ConfigureAwait(false);
        return Result(ReceptionStatuses.ConflictoMismaFactura, receptionId, existing.IdTicket, warnings, [], reused, attemptNumber, slot, startedTimestamp);
    }

    private async Task UpdateReceptionAsync(ReceptionUpdate update, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await _receptions.UpdateAsync(connection, transaction: null, update, cancellationToken);
    }

    private static ImportResult Result(
        string status,
        long receptionId,
        long? ticketId,
        IReadOnlyList<ConversionWarning> warnings,
        IReadOnlyList<string> errors,
        bool reused,
        int attemptNumber,
        AttemptSlot? slot,
        long startedTimestamp)
        => new()
        {
            Status = status,
            ReceptionId = receptionId,
            TicketId = ticketId,
            Warnings = warnings,
            Errors = errors,
            ReusedReception = reused,
            AttemptNumber = attemptNumber,
            AttemptId = slot?.Id,
            DurationMs = Elapsed(startedTimestamp)
        };

    private static ImportResult ArchiveOnly(ReceptionLookup existing, IReadOnlyList<ConversionWarning> warnings, long startedTimestamp)
        => new()
        {
            Status = existing.Estado,
            ReceptionId = existing.IdRecepcion,
            TicketId = existing.IdTicket,
            Warnings = warnings,
            ReusedReception = true,
            AttemptNumber = existing.NumeroIntento,
            ArchiveOnly = true,
            EstadoArchivo = existing.EstadoArchivo,
            DurationMs = Elapsed(startedTimestamp)
        };

    private async Task NoteArchiveRecoveryAsync(long receptionId, string path, CancellationToken cancellationToken)
    {
        if (_tracker is null)
            return;
        await _tracker.RecordArchiveEventAsync(
            TrackingEventTypes.Reconciliacion,
            TrackingSeverity.Information,
            receptionId,
            path,
            "El XML sigue en entrada con un archivado incompleto. No es un intento nuevo de importación.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AttemptSlot?> NoteConnectionFailureAsync(
        TicketImportCommand command,
        string originPath,
        string? hash,
        ParsedFileName? fileName,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_tracker is null)
            return null;
        var slot = _tracker.OpenAttempt(originPath, command.FileName, hash, fileName?.Tienda, fileName?.Tpv);
        await _tracker.RecordConnectionFailureAsync(slot, exception, null, cancellationToken).ConfigureAwait(false);
        return slot;
    }

    private async Task<AttemptSlot?> NoteReceptionAttemptAsync(
        TicketImportCommand command,
        string originPath,
        string? hash,
        ParsedFileName? fileName,
        long receptionId,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        if (_tracker is null)
            return null;
        var slot = _tracker.OpenAttempt(originPath, command.FileName, hash, fileName?.Tienda, fileName?.Tpv);
        await _tracker.RecordReceptionAttemptAsync(slot, receptionId, attemptNumber, cancellationToken).ConfigureAwait(false);
        return slot;
    }

    private async Task TrackTerminalAsync(AttemptSlot? slot, AttemptOutcome outcome, CancellationToken cancellationToken)
    {
        if (_tracker is null || slot is null)
            return;
        await _tracker.RecordOutcomeAsync(slot.Value, outcome, cancellationToken).ConfigureAwait(false);
        _tracker.NoteBusinessOutcome(outcome);
    }

    private async Task TrackCommittedAsync(AttemptSlot? slot, AttemptOutcome outcome, CancellationToken cancellationToken)
    {
        if (_tracker is null || slot is null)
            return;
        await _tracker.CompleteCommittedImportAsync(slot.Value, outcome, alreadyPersisted: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportResult?> ResolveUncertainCommitAsync(
        long receptionId,
        Exception exception,
        AttemptSlot? slot,
        IReadOnlyList<ConversionWarning> warnings,
        bool reused,
        int attemptNumber,
        ParsedFileName? fileName,
        string? hash,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        ReceptionLookup? persisted = null;
        var read = false;
        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken);
            persisted = await _receptions.FindByIdAsync(connection, null, receptionId, cancellationToken).ConfigureAwait(false);
            read = true;
        }
        catch (Exception readEx) when (readEx is not OperationCanceledException)
        {
        }

        var decision = CommitUncertainty.Decide(true, read, persisted?.Estado);
        if (decision == CommitDecision.UsePersisted && persisted is not null)
        {
            await TrackTerminalAsync(slot, OutcomeFor(
                persisted.Estado, receptionId, persisted.IdTicket, attemptNumber, fileName, hash,
                "Resultado leído de TICKET_RECEPCION tras una confirmación incierta.",
                null, null), cancellationToken).ConfigureAwait(false);
            return Result(persisted.Estado, receptionId, persisted.IdTicket, warnings, [], reused, attemptNumber, slot, startedTimestamp);
        }

        if (decision == CommitDecision.Uncertain)
        {
            await TrackTerminalAsync(slot, OutcomeFor(
                TrackingResults.Incierto, receptionId, null, attemptNumber, fileName, hash,
                "La conexión se perdió durante la confirmación y no se pudo leer el estado persistido.",
                "CONEXION", SqlExceptionDetails.ToJson(exception)), cancellationToken).ConfigureAwait(false);
            return new ImportResult
            {
                Status = ReceptionStatuses.ErrorSql,
                ReceptionId = receptionId,
                Warnings = warnings,
                Errors = [exception.Message],
                ReusedReception = reused,
                AttemptNumber = attemptNumber,
                OutcomeUncertain = true,
                AttemptId = slot?.Id,
                DurationMs = Elapsed(startedTimestamp)
            };
        }

        return null;
    }

    private static AttemptOutcome OutcomeFor(
        string statusOrResult,
        long receptionId,
        long? ticketId,
        int attemptNumber,
        ParsedFileName? fileName,
        string? hash,
        string message,
        string? category,
        string? detail,
        int? tienda = null,
        int? tpv = null,
        int? confirmed = null)
    {
        var resultado = statusOrResult is TrackingResults.Incierto
            ? TrackingResults.Incierto
            : ImportClassifications.FromReception(statusOrResult);
        return new AttemptOutcome
        {
            Resultado = resultado,
            Fase = TrackingPhases.Insercion,
            IdRecepcion = receptionId,
            IdTicket = ticketId,
            NumeroIntento = attemptNumber,
            CategoriaError = category,
            Mensaje = message,
            DetalleTecnico = detail,
            Tienda = tienda ?? fileName?.Tienda,
            Tpv = tpv ?? fileName?.Tpv,
            Hash = hash,
            RegistrosConfirmados = confirmed,
            Severidad = resultado switch
            {
                TrackingResults.ErrorSql or TrackingResults.Incierto or TrackingResults.SqlNoDisponible => TrackingSeverity.Error,
                TrackingResults.ErrorXml or TrackingResults.ErrorPermanente or TrackingResults.Conflicto => TrackingSeverity.Warning,
                _ => TrackingSeverity.Information
            }
        };
    }

    private static int CountConfirmed(TicketWriteModel mapped)
        => 1 + mapped.Details.Count + mapped.VatBreakdowns.Count + mapped.TaxKeys.Count
           + mapped.Recipients.Count + mapped.Rectifications.Count;

    private static long Elapsed(long startedTimestamp)
        => (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
}
