using DbInda.Worker.Models;

namespace DbInda.Worker.Persistence;

public static class WarningText
{
    public static string? Join(IReadOnlyList<ConversionWarning> warnings)
    {
        if (warnings.Count == 0)
            return null;

        return string.Join(
            Environment.NewLine,
            warnings.Select(w => $"{w.Code}\t{w.Field}\t{w.Message}\t{w.RawValue}"));
    }

    public static string? JoinXsd(IReadOnlyList<XsdValidationEvent> events)
    {
        if (events.Count == 0)
            return null;

        return string.Join(
            Environment.NewLine,
            events.Select(e =>
                $"{e.Severity}\t{(e.IsKnownIncompatibility ? "INCOMPATIBILIDAD_CONOCIDA" : "DATOS")}\t{e.Message}"));
    }

    public static bool AffectsTicketQuality(ConversionWarning warning)
        => warning.Code switch
        {
            "FILENAME_NO_RECONOCIDO" => false,
            "FILENAME_FECHA_INVALIDA" => false,
            "FILENAME_HORA_INVALIDA" => false,
            "DISCREPANCIA_FILENAME_XML" => false,
            _ => true
        };
}
