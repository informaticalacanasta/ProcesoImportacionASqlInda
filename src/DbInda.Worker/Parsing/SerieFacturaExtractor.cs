using System.Globalization;

namespace DbInda.Worker.Parsing;

public readonly struct SerieFacturaParts
{
    public int? Tienda { get; init; }
    public int? Tpv { get; init; }
    public string? SerieDocumento { get; init; }
}

public static class SerieFacturaExtractor
{
    public static SerieFacturaParts Parse(string? serieFactura)
    {
        if (string.IsNullOrWhiteSpace(serieFactura))
            return new SerieFacturaParts { SerieDocumento = serieFactura };

        var parts = serieFactura.Split('.');
        if (parts.Length < 3)
            return new SerieFacturaParts { SerieDocumento = serieFactura };

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tienda))
            return new SerieFacturaParts { SerieDocumento = serieFactura };
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tpv))
            return new SerieFacturaParts { SerieDocumento = serieFactura };

        var serieDocumento = string.Join(".", parts.Skip(2));
        if (string.IsNullOrWhiteSpace(serieDocumento))
            return new SerieFacturaParts { SerieDocumento = serieFactura };

        return new SerieFacturaParts
        {
            Tienda = tienda,
            Tpv = tpv,
            SerieDocumento = serieDocumento
        };
    }
}
