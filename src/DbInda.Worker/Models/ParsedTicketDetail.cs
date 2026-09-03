namespace DbInda.Worker.Models;

public sealed class ParsedTicketDetail
{
    public int NumLinea { get; init; }
    public string? Descripcion { get; init; }
    public decimal? Cantidad { get; init; }
    public decimal? ImporteUnitario { get; init; }
    public decimal? Descuento { get; init; }
    public decimal? ImporteTotal { get; init; }
    public string? CodigoCentral { get; init; }
    public string? Identificador { get; init; }
    public string? Familia { get; init; }
    public int? Seccion { get; init; }
    public int? Formato { get; init; }
    public bool? Esperpes { get; init; }
    public string? SeccionSala { get; init; }
    public decimal? PvpConsumo { get; init; }
    public bool? EsKit { get; init; }
    public string? IdTiquetlMaster { get; init; }
    public string? IdTiquetl { get; init; }
    public decimal? EquivalenciaUnidad { get; init; }
    public decimal? EquivalenciaPeso { get; init; }
    public decimal? PorcentajeIva { get; init; }
    public decimal? PorcentajeRecargo { get; init; }
}
