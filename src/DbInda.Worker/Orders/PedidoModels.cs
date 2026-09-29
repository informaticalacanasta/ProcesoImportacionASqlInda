namespace DbInda.Worker.Orders;

public sealed class PedidoHeader
{
    public required int StoreId { get; init; }
    public int? WarehouseId { get; init; }
    public required int CashRegisterId { get; init; }
    public int? UserId { get; init; }
    public required long OrderNumber { get; init; }
    public required DateTime OrderedAt { get; init; }
    public DateTime? ServeDate { get; init; }
    public TimeSpan? ServeTime { get; init; }
    public string? TableType { get; init; }
    public bool? EsEncargo { get; init; }
    public int? DeliveryClientId { get; init; }
    public int? CompanyId { get; init; }
    public int? RoomId { get; init; }
    public int? TableId { get; init; }
    public required int LineCount { get; init; }
}

public sealed class PedidoDetail
{
    public required int LineNumber { get; init; }
    public string? IdTiquet { get; init; }
    public int? StoreId { get; init; }
    public int? WarehouseId { get; init; }
    public int? CashRegisterId { get; init; }
    public int? UserId { get; init; }
    public string? Tiempo { get; init; }
    public decimal? Comision { get; init; }
    public string? ClientCardId { get; init; }
    public int? TableId { get; init; }
    public int? RoomId { get; init; }
    public int? CompanyId { get; init; }
    public string? CashCountId { get; init; }
    public string? ArticleId { get; init; }
    public string? VatId { get; init; }
    public string? TariffId { get; init; }
    public string? FormatId { get; init; }
    public string? Description { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Measure { get; init; }
    public bool? EsPerPes { get; init; }
    public int? PersonCount { get; init; }
    public decimal? Pvc { get; init; }
    public decimal? Discount { get; init; }
    public decimal? PricePerWeight { get; init; }
    public decimal? Price { get; init; }
    public decimal? PriceWithVat { get; init; }
    public decimal? TariffPrice { get; init; }
    public decimal? VatPercent { get; init; }
    public decimal? SurchargePercent { get; init; }
    public TimeSpan? StartTime { get; init; }
    public string? Comments { get; init; }
    public string? Observation { get; init; }
    public decimal? BaseAmount { get; init; }
    public decimal? DiscountTotal { get; init; }
    public decimal? Total { get; init; }
    public decimal? VatAmount { get; init; }
    public decimal? SurchargeAmount { get; init; }
    public DateTime? ServeDate { get; init; }
    public TimeSpan? ServeTime { get; init; }
    public string? SupplierId { get; init; }
    public bool? EsEncargo { get; init; }
    public int? DeliveryClientId { get; init; }
    public string? ArticleTcId { get; init; }
    public string? ColorId { get; init; }
    public string? ColorDescription { get; init; }
    public string? BrandDescription { get; init; }
    public string? Size { get; init; }
    public string? BrandId { get; init; }
    public string? BrandPhoto { get; init; }
    public bool? HideQuantity { get; init; }
    public bool? DoNotPrint { get; init; }
    public string? MasterTicketId { get; init; }
    public bool? AlreadyPrintedKitchen { get; init; }
    public bool? PrintKitchen { get; init; }
    public bool? PrintBar { get; init; }
    public bool? HasModifiers { get; init; }
    public bool? HasKitOptions { get; init; }
    public bool? EsKit { get; init; }
    public bool? EsModificador { get; init; }
    public long? OrderNumber { get; init; }
    public string? TableType { get; init; }
    public bool? EsImpreso { get; init; }
    public string? CentralArticleId { get; init; }
    public string? FamilyId { get; init; }
    public bool? Marked { get; init; }
    public string? InternetId { get; init; }
}

public sealed class PedidoDocument
{
    public required PedidoFileIdentity Identity { get; init; }
    public required PedidoHeader Header { get; init; }
    public required IReadOnlyList<PedidoDetail> Lines { get; init; }
}

public sealed class PedidoParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public PedidoDocument? Document { get; init; }

    public static PedidoParseResult Fail(string error) => new() { Success = false, Error = error };

    public static PedidoParseResult Ok(PedidoDocument document) => new() { Success = true, Document = document };
}
