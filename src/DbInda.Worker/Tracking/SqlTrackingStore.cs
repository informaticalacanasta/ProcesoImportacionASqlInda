using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using DbInda.Worker.Models;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Tracking;

public sealed class SqlTrackingStore : ITrackingStore
{
    private readonly SqlConnectionFactory _connections;

    public SqlTrackingStore(SqlConnectionFactory connections)
    {
        _connections = connections;
    }

    public async Task UpsertExecutionAsync(ExecutionRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.IMPORTADOR_EJECUCION AS t
            USING (SELECT @IdEjecucion AS ID_EJECUCION) AS s
                ON t.ID_EJECUCION = s.ID_EJECUCION
            WHEN MATCHED AND t.SECUENCIA < @Secuencia THEN UPDATE SET
                FECHA_SENAL_UTC = @FechaSenalUtc,
                FECHA_PARADA_UTC = COALESCE(@FechaParadaUtc, t.FECHA_PARADA_UTC),
                ESTADO = CASE WHEN @FechaParadaUtc IS NULL THEN t.ESTADO ELSE @Estado END,
                MOTIVO_FINALIZACION = COALESCE(@MotivoFinalizacion, t.MOTIVO_FINALIZACION),
                SECUENCIA = @Secuencia
            WHEN NOT MATCHED THEN INSERT (
                ID_EJECUCION, SECUENCIA, NOMBRE_APLICACION, NOMBRE_INSTANCIA, EQUIPO, ID_PROCESO,
                VERSION_APLICACION, FECHA_INICIO_UTC, FECHA_SENAL_UTC, FECHA_PARADA_UTC, ESTADO, MOTIVO_FINALIZACION)
            VALUES (
                @IdEjecucion, @Secuencia, @NombreAplicacion, @NombreInstancia, @Equipo, @IdProceso,
                @VersionAplicacion, @FechaInicioUtc, @FechaSenalUtc, @FechaParadaUtc, @Estado, @MotivoFinalizacion);
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpsertAttemptAsync(AttemptRow row, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(AttemptMergeSql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Conservado para no prometer aislamiento: si SQL deja la transacción no confirmable,
    /// devolver false no permite confirmar el ticket. El importador no llama a este método.
    /// </summary>
    public async Task<bool> TryUpsertAttemptInTransactionAsync(
        SqlConnection connection,
        IDbTransaction transaction,
        AttemptRow row,
        EventRow? evento,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SAVE TRANSACTION TrackingMark;
            BEGIN TRY
                MERGE dbo.IMPORTADOR_INTENTO AS t
                USING (SELECT @IdIntento AS ID_INTENTO) AS s
                    ON t.ID_INTENTO = s.ID_INTENTO
                WHEN MATCHED AND t.SECUENCIA < @Secuencia AND (
                    t.RESULTADO = 'EN_CURSO'
                    OR t.RESULTADO = @Resultado
                    OR (t.RESULTADO = 'INCIERTO' AND @Resultado NOT IN ('EN_CURSO', 'INCIERTO'))
                ) THEN UPDATE SET
                    ID_RECEPCION = @IdRecepcion,
                    ID_TICKET = @IdTicket,
                    HASH_SHA256 = COALESCE(@HashSha256, t.HASH_SHA256),
                    TIENDA = COALESCE(@Tienda, t.TIENDA),
                    TPV = COALESCE(@Tpv, t.TPV),
                    NUMERO_INTENTO = @NumeroIntento,
                    TIPO_INTENTO = @TipoIntento,
                    FECHA_FIN_UTC = @FechaFinUtc,
                    DURACION_MS = @DuracionMs,
                    FASE = @Fase,
                    RESULTADO = @Resultado,
                    CATEGORIA_ERROR = @CategoriaError,
                    CODIGO_ERROR = @CodigoError,
                    MENSAJE = @Mensaje,
                    ID_DETALLE_TECNICO = @IdDetalleTecnico,
                    FECHA_PROXIMO_REINTENTO_UTC = @FechaProximoReintentoUtc,
                    REGISTROS_CONFIRMADOS = @RegistrosConfirmados,
                    SECUENCIA = @Secuencia
                WHEN NOT MATCHED THEN INSERT (
                    ID_INTENTO, SECUENCIA, ID_EJECUCION, ID_RECEPCION, ID_TICKET, NOMBRE_FICHERO, RUTA_ORIGEN,
                    CORRELACION_ORIGEN, HASH_SHA256, TIENDA, TPV, NUMERO_INTENTO, TIPO_INTENTO, FECHA_INICIO_UTC,
                    FECHA_FIN_UTC, DURACION_MS, FASE, RESULTADO, CATEGORIA_ERROR, CODIGO_ERROR, MENSAJE,
                    ID_DETALLE_TECNICO, FECHA_PROXIMO_REINTENTO_UTC, REGISTROS_CONFIRMADOS)
                VALUES (
                    @IdIntento, @Secuencia, @IdEjecucion, @IdRecepcion, @IdTicket, @NombreFichero, @RutaOrigen,
                    @CorrelacionOrigen, @HashSha256, @Tienda, @Tpv, @NumeroIntento, @TipoIntento, @FechaInicioUtc,
                    @FechaFinUtc, @DuracionMs, @Fase, @Resultado, @CategoriaError, @CodigoError, @Mensaje,
                    @IdDetalleTecnico, @FechaProximoReintentoUtc, @RegistrosConfirmados);

                IF @HasEvent = 1
                BEGIN
                    INSERT INTO dbo.IMPORTADOR_EVENTO (
                        ID_EVENTO, SECUENCIA, FECHA_UTC, SEVERIDAD, TIPO, ID_EJECUCION, ID_INTENTO,
                        ID_RECEPCION, ID_TICKET, RUTA_ORIGEN, FASE, DETALLE)
                    SELECT @IdEvento, @SecuenciaEvento, @FechaEventoUtc, @Severidad, @Tipo, @IdEjecucionEvento, @IdIntentoEvento,
                        @IdRecepcionEvento, @IdTicketEvento, @RutaEvento, @FaseEvento, @Detalle
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.IMPORTADOR_EVENTO WHERE ID_EVENTO = @IdEvento);
                END
                IF XACT_STATE() <> 1
                    THROW 50000, 'La transaccion de negocio ya no es confirmable.', 1;
                SELECT CAST(1 AS bit);
            END TRY
            BEGIN CATCH
                IF XACT_STATE() = 1
                    ROLLBACK TRANSACTION TrackingMark;
                SELECT CAST(0 AS bit);
            END CATCH
            """;

        var args = new
        {
            row.IdIntento,
            row.Secuencia,
            row.IdEjecucion,
            row.IdRecepcion,
            row.IdTicket,
            row.NombreFichero,
            row.RutaOrigen,
            row.CorrelacionOrigen,
            row.HashSha256,
            row.Tienda,
            row.Tpv,
            row.NumeroIntento,
            row.TipoIntento,
            row.FechaInicioUtc,
            row.FechaFinUtc,
            row.DuracionMs,
            row.Fase,
            row.Resultado,
            row.CategoriaError,
            row.CodigoError,
            row.Mensaje,
            row.IdDetalleTecnico,
            row.FechaProximoReintentoUtc,
            row.RegistrosConfirmados,
            HasEvent = evento is not null,
            IdEvento = evento?.IdEvento ?? Guid.Empty,
            SecuenciaEvento = evento?.Secuencia ?? 0,
            FechaEventoUtc = evento?.FechaUtc ?? row.FechaInicioUtc,
            Severidad = evento?.Severidad ?? TrackingSeverity.Information,
            Tipo = evento?.Tipo ?? TrackingEventTypes.ImportacionConfirmada,
            IdEjecucionEvento = evento?.IdEjecucion,
            IdIntentoEvento = evento?.IdIntento,
            IdRecepcionEvento = evento?.IdRecepcion,
            IdTicketEvento = evento?.IdTicket,
            RutaEvento = evento?.RutaOrigen,
            FaseEvento = evento?.Fase,
            Detalle = evento?.Detalle
        };

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, args, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task InsertEventAsync(EventRow row, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.IMPORTADOR_EVENTO (
                ID_EVENTO, SECUENCIA, FECHA_UTC, SEVERIDAD, TIPO, ID_EJECUCION, ID_INTENTO,
                ID_RECEPCION, ID_TICKET, RUTA_ORIGEN, FASE, DETALLE)
            SELECT @IdEvento, @Secuencia, @FechaUtc, @Severidad, @Tipo, @IdEjecucion, @IdIntento,
                @IdRecepcion, @IdTicket, @RutaOrigen, @Fase, @Detalle
            WHERE NOT EXISTS (SELECT 1 FROM dbo.IMPORTADOR_EVENTO WHERE ID_EVENTO = @IdEvento);
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<AttemptRow>> ListOpenAttemptsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                ID_INTENTO AS IdIntento,
                SECUENCIA AS Secuencia,
                ID_EJECUCION AS IdEjecucion,
                ID_RECEPCION AS IdRecepcion,
                ID_TICKET AS IdTicket,
                NOMBRE_FICHERO AS NombreFichero,
                RUTA_ORIGEN AS RutaOrigen,
                CORRELACION_ORIGEN AS CorrelacionOrigen,
                HASH_SHA256 AS HashSha256,
                TIENDA AS Tienda,
                TPV AS Tpv,
                NUMERO_INTENTO AS NumeroIntento,
                TIPO_INTENTO AS TipoIntento,
                FECHA_INICIO_UTC AS FechaInicioUtc,
                FECHA_FIN_UTC AS FechaFinUtc,
                DURACION_MS AS DuracionMs,
                FASE AS Fase,
                RESULTADO AS Resultado
            FROM dbo.IMPORTADOR_INTENTO
            WHERE RESULTADO = 'EN_CURSO';
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AttemptRow>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AttemptRow>> ListRecoverableAttemptsAsync(
        Guid currentExecutionId,
        DateTime staleSignalBeforeUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                i.ID_INTENTO AS IdIntento,
                i.SECUENCIA AS Secuencia,
                i.ID_EJECUCION AS IdEjecucion,
                i.ID_RECEPCION AS IdRecepcion,
                i.ID_TICKET AS IdTicket,
                i.NOMBRE_FICHERO AS NombreFichero,
                i.RUTA_ORIGEN AS RutaOrigen,
                i.CORRELACION_ORIGEN AS CorrelacionOrigen,
                i.HASH_SHA256 AS HashSha256,
                i.TIENDA AS Tienda,
                i.TPV AS Tpv,
                i.NUMERO_INTENTO AS NumeroIntento,
                i.TIPO_INTENTO AS TipoIntento,
                i.FECHA_INICIO_UTC AS FechaInicioUtc,
                i.FECHA_FIN_UTC AS FechaFinUtc,
                i.DURACION_MS AS DuracionMs,
                i.FASE AS Fase,
                i.RESULTADO AS Resultado
            FROM dbo.IMPORTADOR_INTENTO i
            INNER JOIN dbo.IMPORTADOR_EJECUCION e ON e.ID_EJECUCION = i.ID_EJECUCION
            WHERE i.RESULTADO = 'EN_CURSO'
              AND i.ID_EJECUCION <> @CurrentExecutionId
              AND (
                    e.FECHA_PARADA_UTC IS NOT NULL
                    OR e.FECHA_SENAL_UTC < @StaleBefore
                  );
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AttemptRow>(new CommandDefinition(
            sql,
            new { CurrentExecutionId = currentExecutionId, StaleBefore = staleSignalBeforeUtc },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> TryReconcileOpenAttemptAsync(
        Guid attemptId,
        Guid originExecutionId,
        DateTime staleSignalBeforeUtc,
        string resultado,
        string mensaje,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE i
            SET FASE = 'RECONCILIACION',
                RESULTADO = @Resultado,
                MENSAJE = @Mensaje
            FROM dbo.IMPORTADOR_INTENTO i
            INNER JOIN dbo.IMPORTADOR_EJECUCION e ON e.ID_EJECUCION = i.ID_EJECUCION
            WHERE i.ID_INTENTO = @Id
              AND i.RESULTADO = 'EN_CURSO'
              AND i.ID_EJECUCION = @OriginExecutionId
              AND (
                    e.FECHA_PARADA_UTC IS NOT NULL
                    OR e.FECHA_SENAL_UTC < @StaleBefore
                  );
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = attemptId,
                OriginExecutionId = originExecutionId,
                StaleBefore = staleSignalBeforeUtc,
                Resultado = resultado,
                Mensaje = mensaje
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return updated == 1;
    }

    public async Task<ReceptionEvidence?> ReadReceptionEvidenceAsync(long idRecepcion, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ESTADO AS Estado, NUMERO_INTENTO AS NumeroIntento
            FROM dbo.TICKET_RECEPCION
            WHERE ID_RECEPCION = @IdRecepcion;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ReceptionEvidence>(new CommandDefinition(
            sql, new { IdRecepcion = idRecepcion }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ScheduleRetryAsync(Guid attemptId, DateTime nextRetryUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.IMPORTADOR_INTENTO
            SET FECHA_PROXIMO_REINTENTO_UTC = @Next
            WHERE ID_INTENTO = @Id
              AND RESULTADO IN ('ERROR_SQL', 'SQL_NO_DISPONIBLE');
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = attemptId, Next = nextRetryUtc },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return updated == 1;
    }

    public async Task<string?> ReadReceptionEstadoAsync(long idRecepcion, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ESTADO
            FROM dbo.TICKET_RECEPCION
            WHERE ID_RECEPCION = @IdRecepcion;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql, new { IdRecepcion = idRecepcion }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<OperationalReadout> ReadOperationalAsync(
        DateTime archiveStuckBeforeLocal,
        DateTime conflictsSinceLocal,
        CancellationToken cancellationToken)
    {
        const string archives = """
            SELECT COUNT(*)
            FROM dbo.TICKET_RECEPCION
            WHERE ESTADO_ARCHIVO = @Archivando
              AND FECHA_ULTIMO_INTENTO < @Threshold;
            """;
        const string conflicts = """
            SELECT COUNT(*)
            FROM dbo.TICKET_RECEPCION
            WHERE ESTADO = @Conflicto
              AND FECHA_PROCESADO > @Since;
            """;
        const string receptionMarks = """
            SELECT
                TIENDA_FICHERO AS Tienda,
                TPV_FICHERO AS Tpv,
                MAX(FECHA_PROCESADO) AS FechaProcesadoLocal,
                CAST(NULL AS DATETIME2(3)) AS FinIntentoUtc
            FROM dbo.TICKET_RECEPCION
            WHERE ESTADO IN (@Procesado, @ProcesadoAdv)
              AND TIENDA_FICHERO IS NOT NULL
              AND TPV_FICHERO IS NOT NULL
            GROUP BY TIENDA_FICHERO, TPV_FICHERO;
            """;
        const string attemptMarks = """
            SELECT
                TIENDA AS Tienda,
                TPV AS Tpv,
                CAST(NULL AS DATETIME2(3)) AS FechaProcesadoLocal,
                MAX(FECHA_FIN_UTC) AS FinIntentoUtc
            FROM dbo.IMPORTADOR_INTENTO
            WHERE RESULTADO IN ('IMPORTADO', 'IMPORTADO_CON_ADVERTENCIAS')
              AND TIENDA IS NOT NULL
              AND TPV IS NOT NULL
            GROUP BY TIENDA, TPV;
            """;

        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var stuck = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                archives,
                new
                {
                    Archivando = ArchiveStatuses.Archivando,
                    Threshold = archiveStuckBeforeLocal
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var conflictCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                conflicts,
                new { Conflicto = ReceptionStatuses.ConflictoMismaFactura, Since = conflictsSinceLocal },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var fromReception = (await connection.QueryAsync<SourceImportMark>(new CommandDefinition(
                receptionMarks,
                new
                {
                    Procesado = ReceptionStatuses.Procesado,
                    ProcesadoAdv = ReceptionStatuses.ProcesadoConAdvertencias
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
            var fromAttempts = (await connection.QueryAsync<SourceImportMark>(new CommandDefinition(
                attemptMarks, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
            fromReception.AddRange(fromAttempts);
            return new OperationalReadout
            {
                QueriesSucceeded = true,
                ArchivesStuck = stuck,
                NewConflicts = conflictCount,
                Sources = fromReception
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OperationalReadout { QueriesSucceeded = false };
        }
    }

    public async Task<int> DeleteBatchAsync(string sql, DateTime cutoffUtc, int batch, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { Cutoff = cutoffUtc, Batch = batch },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private const string AttemptMergeSql = """
        MERGE dbo.IMPORTADOR_INTENTO AS t
        USING (SELECT @IdIntento AS ID_INTENTO) AS s
            ON t.ID_INTENTO = s.ID_INTENTO
        WHEN MATCHED AND t.SECUENCIA < @Secuencia AND (
            t.RESULTADO = 'EN_CURSO'
            OR t.RESULTADO = @Resultado
            OR (t.RESULTADO = 'INCIERTO' AND @Resultado NOT IN ('EN_CURSO', 'INCIERTO'))
        ) THEN UPDATE SET
            ID_RECEPCION = COALESCE(@IdRecepcion, t.ID_RECEPCION),
            ID_TICKET = COALESCE(@IdTicket, t.ID_TICKET),
            HASH_SHA256 = COALESCE(@HashSha256, t.HASH_SHA256),
            TIENDA = COALESCE(@Tienda, t.TIENDA),
            TPV = COALESCE(@Tpv, t.TPV),
            NUMERO_INTENTO = COALESCE(@NumeroIntento, t.NUMERO_INTENTO),
            TIPO_INTENTO = @TipoIntento,
            FECHA_FIN_UTC = COALESCE(@FechaFinUtc, t.FECHA_FIN_UTC),
            DURACION_MS = COALESCE(@DuracionMs, t.DURACION_MS),
            FASE = @Fase,
            RESULTADO = @Resultado,
            CATEGORIA_ERROR = @CategoriaError,
            CODIGO_ERROR = @CodigoError,
            MENSAJE = @Mensaje,
            ID_DETALLE_TECNICO = COALESCE(@IdDetalleTecnico, t.ID_DETALLE_TECNICO),
            FECHA_PROXIMO_REINTENTO_UTC = COALESCE(@FechaProximoReintentoUtc, t.FECHA_PROXIMO_REINTENTO_UTC),
            REGISTROS_CONFIRMADOS = COALESCE(@RegistrosConfirmados, t.REGISTROS_CONFIRMADOS),
            SECUENCIA = @Secuencia
        WHEN NOT MATCHED THEN INSERT (
            ID_INTENTO, SECUENCIA, ID_EJECUCION, ID_RECEPCION, ID_TICKET, NOMBRE_FICHERO, RUTA_ORIGEN,
            CORRELACION_ORIGEN, HASH_SHA256, TIENDA, TPV, NUMERO_INTENTO, TIPO_INTENTO, FECHA_INICIO_UTC,
            FECHA_FIN_UTC, DURACION_MS, FASE, RESULTADO, CATEGORIA_ERROR, CODIGO_ERROR, MENSAJE,
            ID_DETALLE_TECNICO, FECHA_PROXIMO_REINTENTO_UTC, REGISTROS_CONFIRMADOS)
        VALUES (
            @IdIntento, @Secuencia, @IdEjecucion, @IdRecepcion, @IdTicket, @NombreFichero, @RutaOrigen,
            @CorrelacionOrigen, @HashSha256, @Tienda, @Tpv, @NumeroIntento, @TipoIntento, @FechaInicioUtc,
            @FechaFinUtc, @DuracionMs, @Fase, @Resultado, @CategoriaError, @CodigoError, @Mensaje,
            @IdDetalleTecnico, @FechaProximoReintentoUtc, @RegistrosConfirmados);
        """;
}
