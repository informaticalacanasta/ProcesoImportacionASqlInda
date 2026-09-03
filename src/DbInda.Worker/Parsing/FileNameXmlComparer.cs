using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public static class FileNameXmlComparer
{
    public static IReadOnlyList<ConversionWarning> Compare(ParsedFileName fileName, ParsedTicket ticket)
    {
        if (!fileName.PatternMatched)
            return [];

        var warnings = new List<ConversionWarning>();
        CompareValue("NIF", fileName.NifEmisor, ticket.NifEmisor, warnings);
        CompareValue("NumFactura", fileName.NumFactura, ticket.NumFactura, warnings);
        CompareValue("Tienda", Format(fileName.Tienda), Format(ticket.Tienda), warnings);
        CompareValue("TPV", Format(fileName.Tpv), Format(ticket.Tpv), warnings);
        CompareValue("Fecha", fileName.Fecha?.ToString("dd-MM-yyyy"), ticket.FechaExpedicion?.ToString("dd-MM-yyyy"), warnings);
        CompareValue("Hora", fileName.Hora?.ToString("HH:mm:ss"), ticket.HoraExpedicion?.ToString("HH:mm:ss"), warnings);

        if (fileName.Importe is not null && ticket.ImporteTotal is not null && fileName.Importe != ticket.ImporteTotal)
        {
            warnings.Add(new ConversionWarning
            {
                Code = "DISCREPANCIA_FILENAME_XML",
                Field = "Importe",
                Message = $"El importe del nombre ({fileName.Importe}) no coincide con el XML ({ticket.ImporteTotal}). Se usa el XML.",
                RawValue = fileName.Importe.ToString()
            });
        }

        return warnings;
    }

    private static void CompareValue(string field, string? fromName, string? fromXml, List<ConversionWarning> warnings)
    {
        if (fromName is null || fromXml is null)
            return;
        if (string.Equals(fromName, fromXml, StringComparison.OrdinalIgnoreCase))
            return;

        warnings.Add(new ConversionWarning
        {
            Code = "DISCREPANCIA_FILENAME_XML",
            Field = field,
            Message = $"El {field} del nombre ({fromName}) no coincide con el XML ({fromXml}). Se usa el XML.",
            RawValue = fromName
        });
    }

    private static string? Format(int? value) => value?.ToString();
}
