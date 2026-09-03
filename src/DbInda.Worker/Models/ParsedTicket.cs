namespace DbInda.Worker.Models;

public sealed class ParsedTicket
{
    public string? IdVersionTbai { get; init; }
    public string? NifEmisor { get; init; }
    public string? RazonSocialEmisor { get; init; }
    public string? SerieFactura { get; init; }
    public string? NumFactura { get; init; }
    public DateOnly? FechaExpedicion { get; init; }
    public TimeOnly? HoraExpedicion { get; init; }
    public int? Tienda { get; init; }
    public int? Tpv { get; init; }
    public int? NVendedor { get; init; }
    public string? DVendedor { get; init; }
    public int? NFormaPago { get; init; }
    public string? DFormaPago { get; init; }
    public decimal? ImporteTotal { get; init; }
    public bool? FacturaSimplificada { get; init; }
    public bool? FacturaSustitucionSimplificada { get; init; }
    public string? EmitidaPor { get; init; }
    public string? DescripcionFactura { get; init; }
    public DateOnly? FechaOperacion { get; init; }
    public decimal? RetencionSoportada { get; init; }
    public decimal? BaseImponibleACoste { get; init; }
    public long? NEncargo { get; init; }
    public int? IdSala { get; init; }
    public string? TipoMesa { get; init; }
    public int? IdMesa { get; init; }
    public string? IdClient { get; init; }
    public string? NumSerieDispositivo { get; init; }
    public string? SerieFacturaAnterior { get; init; }
    public string? NumFacturaAnterior { get; init; }
    public DateOnly? FechaFacturaAnterior { get; init; }
    public string? HashFirmaFacturaAnterior { get; init; }

    public IReadOnlyList<ParsedTicketDetail> Details { get; init; } = [];
    public IReadOnlyList<ParsedVatBreakdown> VatBreakdowns { get; init; } = [];
    public IReadOnlyList<ParsedTaxKey> TaxKeys { get; init; } = [];
    public IReadOnlyList<ParsedRecipient> Recipients { get; init; } = [];
    public IReadOnlyList<ParsedRectification> Rectifications { get; init; } = [];
}
