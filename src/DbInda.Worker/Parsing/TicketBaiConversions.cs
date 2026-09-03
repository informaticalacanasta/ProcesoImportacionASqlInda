using System.Globalization;
using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class TicketBaiConversions
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly List<ConversionWarning> _warnings;

    public TicketBaiConversions(List<ConversionWarning> warnings)
    {
        _warnings = warnings;
    }

    public string? Text(string? raw)
    {
        if (raw is null)
            return null;

        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public DateOnly? Date(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (DateOnly.TryParseExact(value, "dd-MM-yyyy", Invariant, DateTimeStyles.None, out var date))
            return date;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"La fecha '{value}' no tiene formato dd-MM-yyyy.");
        return null;
    }

    public TimeOnly? Time(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (TimeOnly.TryParseExact(value, "HH:mm:ss", Invariant, DateTimeStyles.None, out var time))
            return time;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"La hora '{value}' no tiene formato HH:mm:ss.");
        return null;
    }

    public decimal? Decimal(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (decimal.TryParse(value, NumberStyles.Number, Invariant, out var number))
            return number;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"El decimal '{value}' no es convertible.");
        return null;
    }

    public bool? SiNo(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (value == "S")
            return true;
        if (value == "N")
            return false;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"El valor '{value}' no es S/N.");
        return null;
    }

    public int? Int32(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (int.TryParse(value, NumberStyles.Integer, Invariant, out var number))
            return number;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"El entero '{value}' no es convertible.");
        return null;
    }

    public long? Int64(string? raw, string field)
    {
        var value = Text(raw);
        if (value is null)
            return null;

        if (long.TryParse(value, NumberStyles.Integer, Invariant, out var number))
            return number;

        Warn(field, value, "VALOR_NO_CONVERTIBLE", $"El entero largo '{value}' no es convertible.");
        return null;
    }

    public void WarnLength(string field, string value, int maxLength)
    {
        if (value.Length > maxLength)
        {
            Warn(
                field,
                value,
                "LONGITUD_EXCEDIDA",
                $"El campo {field} tiene {value.Length} caracteres (máximo XSD {maxLength}). No se trunca.");
        }
    }

    private void Warn(string field, string? rawValue, string code, string message)
    {
        _warnings.Add(new ConversionWarning
        {
            Code = code,
            Field = field,
            Message = message,
            RawValue = rawValue
        });
    }
}
