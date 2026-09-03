using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Models;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Processing;

public sealed class TicketImportProcessor
{
    private readonly SqlConnectionFactory _connections;
    private readonly ReceptionRepository _receptions;
    private readonly TicketRepository _tickets;

    public TicketImportProcessor(
        SqlConnectionFactory connections,
        ReceptionRepository receptions,
        TicketRepository tickets)
    {
        _connections = connections;
        _receptions = receptions;
        _tickets = tickets;
    }

    public async Task<ImportResult> ImportAsync(TicketImportCommand command, CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
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
                return new ImportResult
                {
                    Status = existing.Estado,
                    ReceptionId = existing.IdRecepcion,
                    TicketId = existing.IdTicket,
                    Warnings = parseWarnings,
                    ReusedReception = true,
                    AttemptNumber = existing.NumeroIntento,
                    ArchiveOnly = true,
                    EstadoArchivo = existing.EstadoArchivo
                };
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
                        return new ImportResult
                        {
                            Status = existing.Estado,
                            ReceptionId = existing.IdRecepcion,
                            TicketId = existing.IdTicket,
                            Warnings = parseWarnings,
                            ReusedReception = true,
                            AttemptNumber = existing.NumeroIntento,
                            ArchiveOnly = true,
                            EstadoArchivo = existing.EstadoArchivo
                        };
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
            return new ImportResult
            {
                Status = ReceptionStatuses.ErrorSql,
                Warnings = parseWarnings,
                Errors = [ex.Message],
                SqlUnavailable = true
            };
        }

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

            return Result(ReceptionStatuses.ErrorXml, receptionId, null, parseWarnings,
                command.Parse.Errors.Count == 0 ? ["El XML no pudo parsearse."] : command.Parse.Errors,
                reused, attemptNumber);
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

            return Result(ReceptionStatuses.ErrorPermanente, receptionId, null, parseWarnings,
                ["No se pudo calcular HASH_SHA256 porque el fichero no tiene bytes."], reused, attemptNumber);
        }

        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken);

            var duplicate = await _tickets.FindByHashAsync(connection, null, hash, cancellationToken);
            if (duplicate is not null)
                return await MarkDuplicateAsync(receptionId, duplicate, parseWarnings, reused, attemptNumber, cancellationToken);

            var identity = mapped.NifEmisor is not null && mapped.NumFactura is not null && mapped.FechaExpedicion is not null
                ? await _tickets.FindByIdentityAsync(
                    connection, null, mapped.NifEmisor, mapped.SerieFactura, mapped.NumFactura, mapped.FechaExpedicion.Value,
                    mapped.Tienda, mapped.Tpv, cancellationToken)
                : null;
            if (identity is not null)
                return await MarkConflictAsync(receptionId, identity, parseWarnings, reused, attemptNumber, cancellationToken);

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

                await transaction.CommitAsync(cancellationToken);

                return Result(receptionStatus, receptionId, ticketId, parseWarnings, [], reused, attemptNumber);
            }
            catch (Exception ex) when (SqlUniqueConstraint.IsTicketHashDuplicate(ex))
            {
                await transaction.RollbackAsync(cancellationToken);
                var existingTicket = await _tickets.FindByHashAsync(connection, transaction: null, hash, cancellationToken);
                if (existingTicket is null)
                    throw;

                return await MarkDuplicateAsync(receptionId, existingTicket, parseWarnings, reused, attemptNumber, cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
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

            return Result(ReceptionStatuses.ErrorSql, receptionId, null, parseWarnings, [ex.Message], reused, attemptNumber);
        }
    }

    public async Task BeginArchiveAsync(long idRecepcion, string rutaDestinoPrevista, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await _receptions.MarkArchivingAsync(connection, null, idRecepcion, rutaDestinoPrevista, cancellationToken);
    }

    public async Task CompleteArchiveAsync(long idRecepcion, string rutaFinal, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);
        await _receptions.MarkArchivedAsync(connection, null, idRecepcion, rutaFinal, cancellationToken);
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

        return Result(ReceptionStatuses.Duplicado, receptionId, existing.IdTicket, warnings, [], reused, attemptNumber);
    }

    private async Task<ImportResult> MarkConflictAsync(
        long receptionId,
        ExistingTicketRef existing,
        List<ConversionWarning> warnings,
        bool reused,
        int attemptNumber,
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

        return Result(ReceptionStatuses.ConflictoMismaFactura, receptionId, existing.IdTicket, warnings, [], reused, attemptNumber);
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
        int attemptNumber)
        => new()
        {
            Status = status,
            ReceptionId = receptionId,
            TicketId = ticketId,
            Warnings = warnings,
            Errors = errors,
            ReusedReception = reused,
            AttemptNumber = attemptNumber
        };
}
