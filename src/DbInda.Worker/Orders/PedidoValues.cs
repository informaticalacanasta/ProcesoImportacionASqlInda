using System.Globalization;

namespace DbInda.Worker.Orders;

public static class PedidoValues
{
    public static string? Text(string raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    public static int? Int32(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new FormatException();
        return value;
    }

    public static long? Int64(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw new FormatException();
        return value;
    }

    public static decimal? Decimal(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;

        var comma = text.LastIndexOf(',');
        var dot = text.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
            text = comma > dot
                ? text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.')
                : text.Replace(",", "", StringComparison.Ordinal);
        else if (comma >= 0)
            text = text.Replace(',', '.');

        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new FormatException();
        return value;
    }

    public static bool? Boolean(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        return text.ToLowerInvariant() switch
        {
            "true" or "verdadero" or "1" => true,
            "false" or "falso" or "0" => false,
            _ => throw new FormatException()
        };
    }

    public static DateTime? Date(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        if (!DateTime.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException();
        return value;
    }

    public static TimeSpan? Time(string raw)
    {
        var text = Text(raw);
        if (text is null)
            return null;
        if (!TimeSpan.TryParseExact(text, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var value))
            throw new FormatException();
        return value;
    }
}
