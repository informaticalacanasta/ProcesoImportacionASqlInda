namespace DbInda.Worker.Models;

public sealed class ParsedRectification
{
    public int NumOrden { get; init; }
    public string? Codigo { get; init; }
    public string? Tipo { get; init; }
    public decimal? BaseRectificada { get; init; }
    public decimal? CuotaRectificada { get; init; }
    public decimal? CuotaRecargoRectificada { get; init; }
    public string? SerieFactura { get; init; }
    public string? NumFactura { get; init; }
    public DateOnly? FechaExpedicion { get; init; }
}
