namespace DbInda.Worker.Models;

public static class ReceptionStatuses
{
    public const string Pendiente = "PENDIENTE";
    public const string Procesando = "PROCESANDO";
    public const string Procesado = "PROCESADO";
    public const string ProcesadoConAdvertencias = "PROCESADO_CON_ADVERTENCIAS";
    public const string Duplicado = "DUPLICADO";
    public const string ConflictoMismaFactura = "CONFLICTO_MISMA_FACTURA";
    public const string ErrorTemporal = "ERROR_TEMPORAL";
    public const string ErrorXml = "ERROR_XML";
    public const string ErrorSql = "ERROR_SQL";
    public const string ErrorPermanente = "ERROR_PERMANENTE";
}

public static class ArchiveStatuses
{
    public const string Pendiente = "PENDIENTE";
    public const string Archivando = "ARCHIVANDO";
    public const string Archivado = "ARCHIVADO";
    public const string ErrorArchivo = "ERROR_ARCHIVO";
}

public static class TicketQualityStatuses
{
    public const string Ok = "OK";
    public const string ConAdvertencias = "CON_ADVERTENCIAS";
    public const string Incompleto = "INCOMPLETO";
}
