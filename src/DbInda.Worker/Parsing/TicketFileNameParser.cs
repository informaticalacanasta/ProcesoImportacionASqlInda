using System.Text.RegularExpressions;
using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class TicketFileNameParser
{
    private const RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Fact_{NIF}_{token}_{tienda}-{tpv}-{num}_{yyyyMMdd}_{HHmmss}_{importe}[_{sufijo}].xml
    private static readonly Regex PatternWithNif = new(
        @"^Fact_(?<nif>[^_]+)_(?<token>[^_]+)_(?<tienda>[^-]+)-(?<tpv>[^-]+)-(?<num>[^_]+)_(?<fecha>\d{8})_(?<hora>\d{6})_(?<importe>[^_]+)(?:_(?<suffix>.+))?\.xml$",
        Options);

    // Fact_{token}_{tienda}-{tpv}-{num}_{yyyyMMdd}_{HHmmss}_{importe}[_{sufijo}].xml
    private static readonly Regex PatternWithoutNif = new(
        @"^Fact_(?<token>[^_]+)_(?<tienda>[^-]+)-(?<tpv>[^-]+)-(?<num>[^_]+)_(?<fecha>\d{8})_(?<hora>\d{6})_(?<importe>[^_]+)(?:_(?<suffix>.+))?\.xml$",
        Options);

    public ParsedFileName Parse(string fileName)
    {
        var original = Path.GetFileName(fileName);
        var warnings = new List<ConversionWarning>();
        var conversions = new TicketBaiConversions(warnings);

        var withNif = PatternWithNif.Match(original);
        if (withNif.Success)
            return Map(original, withNif, conversions, warnings);

        var withoutNif = PatternWithoutNif.Match(original);
        if (withoutNif.Success)
            return Map(original, withoutNif, conversions, warnings);

        warnings.Add(new ConversionWarning
        {
            Code = "FILENAME_NO_RECONOCIDO",
            Field = "NombreFichero",
            Message = "El nombre del fichero no coincide con los patrones observados Fact_[NIF_]token_tienda-tpv-num_yyyyMMdd_HHmmss_importe[_sufijo].xml.",
            RawValue = original
        });

        return new ParsedFileName
        {
            OriginalFileName = original,
            PatternMatched = false,
            Warnings = warnings
        };
    }

    private static ParsedFileName Map(
        string original,
        Match match,
        TicketBaiConversions conversions,
        List<ConversionWarning> warnings)
    {
        var fechaRaw = match.Groups["fecha"].Value;
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

        var horaRaw = match.Groups["hora"].Value;
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

        var nifGroup = match.Groups["nif"];
        return new ParsedFileName
        {
            OriginalFileName = original,
            PatternMatched = true,
            NifEmisor = nifGroup.Success ? conversions.Text(nifGroup.Value) : null,
            TokenIntermedio = conversions.Text(match.Groups["token"].Value),
            Tienda = conversions.Int32(match.Groups["tienda"].Value, "TiendaFichero"),
            Tpv = conversions.Int32(match.Groups["tpv"].Value, "TpvFichero"),
            NumFactura = conversions.Text(match.Groups["num"].Value),
            Fecha = fecha,
            Hora = hora,
            Importe = conversions.Decimal(match.Groups["importe"].Value, "ImporteFichero"),
            Warnings = warnings
        };
    }
}
