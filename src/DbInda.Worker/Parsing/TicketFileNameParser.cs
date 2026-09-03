using System.Text.RegularExpressions;
using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class TicketFileNameParser
{
    private static readonly Regex Pattern = new(
        @"^Fact_([^_]+)_([^_]+)_([^-]+)-([^-]+)-([^_]+)_(\d{8})_(\d{6})_([^_]+)_(.+)\.xml$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ParsedFileName Parse(string fileName)
    {
        var original = Path.GetFileName(fileName);
        var warnings = new List<ConversionWarning>();
        var conversions = new TicketBaiConversions(warnings);
        var match = Pattern.Match(original);

        if (!match.Success)
        {
            warnings.Add(new ConversionWarning
            {
                Code = "FILENAME_NO_RECONOCIDO",
                Field = "NombreFichero",
                Message = "El nombre del fichero no coincide con el patrón observado Fact_NIF_token_tienda-tpv-num_yyyyMMdd_HHmmss_importe_*.xml.",
                RawValue = original
            });

            return new ParsedFileName
            {
                OriginalFileName = original,
                PatternMatched = false,
                Warnings = warnings
            };
        }

        var fechaRaw = match.Groups[6].Value;
        DateOnly? fecha = null;
        if (DateOnly.TryParseExact(fechaRaw, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsedFecha))
            fecha = parsedFecha;
        else
        {
            warnings.Add(new ConversionWarning
            {
                Code = "FILENAME_FECHA_INVALIDA",
                Field = "FechaFichero",
                Message = "La fecha del nombre no es yyyyMMdd convertible.",
                RawValue = fechaRaw
            });
        }

        var horaRaw = match.Groups[7].Value;
        TimeOnly? hora = null;
        if (TimeOnly.TryParseExact(horaRaw, "HHmmss", null, System.Globalization.DateTimeStyles.None, out var parsedHora))
            hora = parsedHora;
        else
        {
            warnings.Add(new ConversionWarning
            {
                Code = "FILENAME_HORA_INVALIDA",
                Field = "HoraFichero",
                Message = "La hora del nombre no es HHmmss convertible.",
                RawValue = horaRaw
            });
        }

        return new ParsedFileName
        {
            OriginalFileName = original,
            PatternMatched = true,
            NifEmisor = conversions.Text(match.Groups[1].Value),
            TokenIntermedio = conversions.Text(match.Groups[2].Value),
            Tienda = conversions.Int32(match.Groups[3].Value, "TiendaFichero"),
            Tpv = conversions.Int32(match.Groups[4].Value, "TpvFichero"),
            NumFactura = conversions.Text(match.Groups[5].Value),
            Fecha = fecha,
            Hora = hora,
            Importe = conversions.Decimal(match.Groups[8].Value, "ImporteFichero"),
            Warnings = warnings
        };
    }
}
