using DbInda.Worker.Files;
using DbInda.Worker.Models;

namespace DbInda.Worker.Processing;

public static class ReceptionLifecycle
{
    public static bool IsRecoverable(string estado)
        => estado is ReceptionStatuses.ErrorSql
            or ReceptionStatuses.Pendiente
            or ReceptionStatuses.Procesando;

    public static bool IsSuccessArchive(string estado)
        => estado is ReceptionStatuses.Procesado
            or ReceptionStatuses.ProcesadoConAdvertencias
            or ReceptionStatuses.Duplicado
            or ReceptionStatuses.ConflictoMismaFactura;

    public static bool IsDefinitiveXmlError(string estado)
        => estado is ReceptionStatuses.ErrorXml
            or ReceptionStatuses.ErrorPermanente;

    public static bool ShouldArchive(string estadoImportacion)
        => IsSuccessArchive(estadoImportacion) || IsDefinitiveXmlError(estadoImportacion);

    public static bool IsIncompleteArchive(string estadoImportacion, string estadoArchivo)
        => ShouldArchive(estadoImportacion)
           && estadoArchivo is ArchiveStatuses.Pendiente or ArchiveStatuses.Archivando;

    public static ArchiveKind ArchiveKindFor(string estadoImportacion)
        => IsDefinitiveXmlError(estadoImportacion) ? ArchiveKind.Error : ArchiveKind.Processed;
}
