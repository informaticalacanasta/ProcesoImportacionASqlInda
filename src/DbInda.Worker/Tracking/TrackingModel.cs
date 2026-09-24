using System.Text.Json;
using DbInda.Worker.Models;

namespace DbInda.Worker.Tracking;

public static class TrackingResults
{
    public const string EnCurso = "EN_CURSO";
    public const string Importado = "IMPORTADO";
    public const string ImportadoConAdvertencias = "IMPORTADO_CON_ADVERTENCIAS";
    public const string Duplicado = "DUPLICADO";
    public const string Conflicto = "CONFLICTO";
    public const string ErrorXml = "ERROR_XML";
    public const string ErrorPermanente = "ERROR_PERMANENTE";
    public const string ErrorSql = "ERROR_SQL";
    public const string SqlNoDisponible = "SQL_NO_DISPONIBLE";
    public const string Incierto = "INCIERTO";
}

public static class TrackingAttemptKinds
{
    public const string Conexion = "CONEXION";
    public const string Recepcion = "RECEPCION";
}

public static class TrackingPhases
{
    public const string Conexion = "CONEXION";
    public const string Recepcion = "RECEPCION";
    public const string Insercion = "INSERCION";
    public const string Archivo = "ARCHIVO";
    public const string Reconciliacion = "RECONCILIACION";
    public const string Servicio = "SERVICIO";
}

public static class TrackingEventTypes
{
    public const string IntentoIniciado = "INTENTO_INICIADO";
    public const string Error = "ERROR";
    public const string ErrorReintentoProgramado = "ERROR_REINTENTO_PROGRAMADO";
    public const string ImportacionConfirmada = "IMPORTACION_CONFIRMADA";
    public const string Duplicado = "DUPLICADO";
    public const string Conflicto = "CONFLICTO";
    public const string ArchivadoIniciado = "ARCHIVADO_INICIADO";
    public const string ArchivadoCompletado = "ARCHIVADO_COMPLETADO";
    public const string ArchivadoFallido = "ARCHIVADO_FALLIDO";
    public const string Reconciliacion = "RECONCILIACION";
    public const string SqlInaccesible = "SQL_INACCESIBLE";
    public const string SqlRecuperado = "SQL_RECUPERADO";
    public const string ServicioIniciado = "SERVICIO_INICIADO";
    public const string ServicioParado = "SERVICIO_PARADO";
}

public static class TrackingSeverity
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
}

public static class ExecutionStates
{
    public const string Activa = "ACTIVA";
    public const string Detenida = "DETENIDA";
}

public static class ImportClassifications
{
    public static bool IsNewImport(string resultado)
        => resultado is TrackingResults.Importado or TrackingResults.ImportadoConAdvertencias;

    /// <summary>
    /// Una secuencia mayor solo refresca el mismo resultado, cierra un intento abierto
    /// o sustituye un INCIERTO por un resultado demostrado. No reabre un cierre.
    /// </summary>
    public static bool AllowsSequenceUpdate(string? currentResult, long currentSequence, string? incomingResult, long incomingSequence)
    {
        if (incomingSequence <= currentSequence || string.IsNullOrEmpty(incomingResult))
            return false;
        if (string.Equals(currentResult, TrackingResults.EnCurso, StringComparison.Ordinal)
            || string.Equals(currentResult, incomingResult, StringComparison.Ordinal))
            return true;
        return string.Equals(currentResult, TrackingResults.Incierto, StringComparison.Ordinal)
            && incomingResult is not (TrackingResults.EnCurso or TrackingResults.Incierto);
    }

    public static string FromReception(string estado) => estado switch
    {
        ReceptionStatuses.Procesado => TrackingResults.Importado,
        ReceptionStatuses.ProcesadoConAdvertencias => TrackingResults.ImportadoConAdvertencias,
        ReceptionStatuses.Duplicado => TrackingResults.Duplicado,
        ReceptionStatuses.ConflictoMismaFactura => TrackingResults.Conflicto,
        ReceptionStatuses.ErrorXml => TrackingResults.ErrorXml,
        ReceptionStatuses.ErrorPermanente => TrackingResults.ErrorPermanente,
        ReceptionStatuses.ErrorSql => TrackingResults.ErrorSql,
        _ => TrackingResults.Incierto
    };

    public static string EventFor(string resultado) => resultado switch
    {
        TrackingResults.Importado or TrackingResults.ImportadoConAdvertencias => TrackingEventTypes.ImportacionConfirmada,
        TrackingResults.Duplicado => TrackingEventTypes.Duplicado,
        TrackingResults.Conflicto => TrackingEventTypes.Conflicto,
        TrackingResults.SqlNoDisponible => TrackingEventTypes.SqlInaccesible,
        _ => TrackingEventTypes.Error
    };

    public static DateTimeOffset? LatestSuccessfulImport(IEnumerable<DateTimeOffset?> finishedUtc, IEnumerable<string> resultados)
    {
        DateTimeOffset? latest = null;
        using var times = finishedUtc.GetEnumerator();
        using var states = resultados.GetEnumerator();
        while (times.MoveNext() && states.MoveNext())
        {
            if (!IsNewImport(states.Current) || times.Current is null)
                continue;
            if (latest is null || times.Current > latest)
                latest = times.Current;
        }

        return latest;
    }
}

public enum CommitDecision
{
    TreatAsFailure,
    UsePersisted,
    Uncertain
}

public static class CommitUncertainty
{
    public static CommitDecision Decide(bool commitStarted, bool readSucceeded, string? estado)
    {
        if (!commitStarted)
            return CommitDecision.TreatAsFailure;
        if (!readSucceeded)
            return CommitDecision.Uncertain;
        if (estado is ReceptionStatuses.Procesado
            or ReceptionStatuses.ProcesadoConAdvertencias
            or ReceptionStatuses.Duplicado
            or ReceptionStatuses.ConflictoMismaFactura
            or ReceptionStatuses.ErrorXml
            or ReceptionStatuses.ErrorPermanente)
            return CommitDecision.UsePersisted;
        return CommitDecision.TreatAsFailure;
    }
}

public static class OpenAttemptResolution
{
    public static string Resolve(string? receptionEstado)
    {
        if (string.IsNullOrEmpty(receptionEstado))
            return TrackingResults.Incierto;
        if (receptionEstado is ReceptionStatuses.Pendiente or ReceptionStatuses.Procesando)
            return TrackingResults.Incierto;
        var mapped = ImportClassifications.FromReception(receptionEstado);
        return mapped == TrackingResults.Incierto ? TrackingResults.Incierto : mapped;
    }
}

public static class TextClip
{
    public static string? Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        if (max <= 1)
            return value[..max];
        return value[..(max - 1)] + "…";
    }
}

public static class TrackingRetentionPolicy
{
    public static bool IsProtectedAttempt(string? resultado)
        => resultado is TrackingResults.EnCurso or TrackingResults.Incierto;

    public const string DeleteAttemptsSql = """
        DELETE TOP (@Batch)
        FROM dbo.IMPORTADOR_INTENTO
        WHERE FECHA_INICIO_UTC < @Cutoff
          AND RESULTADO NOT IN ('EN_CURSO', 'INCIERTO');
        """;

    public const string DeleteEventsSql = """
        DELETE TOP (@Batch) e
        FROM dbo.IMPORTADOR_EVENTO e
        WHERE e.FECHA_UTC < @Cutoff
          AND NOT EXISTS (
                SELECT 1
                FROM dbo.IMPORTADOR_INTENTO a
                WHERE a.ID_INTENTO = e.ID_INTENTO
                  AND a.RESULTADO IN ('EN_CURSO', 'INCIERTO'));
        """;

    public const string DeleteExecutionsSql = """
        DELETE TOP (@Batch)
        FROM dbo.IMPORTADOR_EJECUCION
        WHERE FECHA_INICIO_UTC < @Cutoff
          AND FECHA_PARADA_UTC IS NOT NULL
          AND ESTADO = 'DETENIDA';
        """;
}

public static class SqlExceptionDetails
{
    public static IReadOnlyDictionary<string, object?> Describe(Exception exception)
    {
        var sql = FindSql(exception);
        if (sql is null)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().FullName,
                ["message"] = TextClip.Clip(exception.Message, 1000)
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = sql.GetType().FullName,
            ["message"] = TextClip.Clip(sql.Message, 1000),
            ["number"] = sql.Number,
            ["state"] = sql.State,
            ["class"] = sql.Class,
            ["procedure"] = string.IsNullOrEmpty(sql.Procedure) ? null : sql.Procedure,
            ["line"] = sql.LineNumber
        };
    }

    public static string ToJson(Exception exception)
        => JsonSerializer.Serialize(Describe(exception));

    private static Microsoft.Data.SqlClient.SqlException? FindSql(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException sql)
                return sql;
        }

        return null;
    }
}
