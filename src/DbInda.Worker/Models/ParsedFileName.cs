namespace DbInda.Worker.Models;

public sealed class ParsedFileName
{
    public required string OriginalFileName { get; init; }
    public bool PatternMatched { get; init; }
    public string? NifEmisor { get; init; }
    public string? TokenIntermedio { get; init; }
    public int? Tienda { get; init; }
    public int? Tpv { get; init; }
    public string? NumFactura { get; init; }
    public DateOnly? Fecha { get; init; }
    public TimeOnly? Hora { get; init; }
    public decimal? Importe { get; init; }
    public IReadOnlyList<ConversionWarning> Warnings { get; init; } = [];
}
