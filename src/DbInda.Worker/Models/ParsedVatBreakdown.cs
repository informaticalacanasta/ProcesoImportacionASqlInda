namespace DbInda.Worker.Models;

public sealed class ParsedVatBreakdown
{
    public int NumOrden { get; init; }
    public string? TipoDesglose { get; init; }
    public string? TipoSujecion { get; init; }
    public string? TipoNoExenta { get; init; }
    public string? CausaExencion { get; init; }
    public string? CausaNoSujeta { get; init; }
    public decimal? BaseImponible { get; init; }
    public decimal? TipoImpositivo { get; init; }
    public decimal? CuotaImpuesto { get; init; }
    public decimal? TipoRecargoEquivalencia { get; init; }
    public decimal? CuotaRecargoEquivalencia { get; init; }
    public bool? OperacionRecargoOSimplificado { get; init; }
    public decimal? ImporteNoSujeta { get; init; }
}
