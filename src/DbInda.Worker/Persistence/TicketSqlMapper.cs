using DbInda.Worker.Models;

namespace DbInda.Worker.Persistence;

public sealed class TicketWriteModel
{
    public required ParsedTicket Source { get; init; }
    public required List<ConversionWarning> Warnings { get; init; }
    public required string? NifEmisor { get; init; }
    public required string? RazonSocialEmisor { get; init; }
    public required string? SerieFactura { get; init; }
    public required string? NumFactura { get; init; }
    public DateOnly? FechaExpedicion { get; init; }
    public TimeOnly? HoraExpedicion { get; init; }
    public int? Tienda { get; init; }
    public int? Tpv { get; init; }
    public int? NVendedor { get; init; }
    public required string? DVendedor { get; init; }
    public int? NFormaPago { get; init; }
    public required string? DFormaPago { get; init; }
    public decimal? ImporteTotal { get; init; }
    public bool? FacturaSimplificada { get; init; }
    public bool? FacturaSustitucionSimplificada { get; init; }
    public required string? EmitidaPor { get; init; }
    public required string? DescripcionFactura { get; init; }
    public DateOnly? FechaOperacion { get; init; }
    public decimal? RetencionSoportada { get; init; }
    public decimal? BaseImponibleACoste { get; init; }
    public required string? IdVersionTbai { get; init; }
    public long? NEncargo { get; init; }
    public int? IdSala { get; init; }
    public required string? TipoMesa { get; init; }
    public int? IdMesa { get; init; }
    public required string? IdClient { get; init; }
    public required string? NumSerieDispositivo { get; init; }
    public required string? SerieFacturaAnterior { get; init; }
    public required string? NumFacturaAnterior { get; init; }
    public DateOnly? FechaFacturaAnterior { get; init; }
    public required string? HashFirmaFacturaAnterior { get; init; }
    public required IReadOnlyList<DetailWriteModel> Details { get; init; }
    public required IReadOnlyList<VatWriteModel> VatBreakdowns { get; init; }
    public required IReadOnlyList<TaxKeyWriteModel> TaxKeys { get; init; }
    public required IReadOnlyList<RecipientWriteModel> Recipients { get; init; }
    public required IReadOnlyList<RectificationWriteModel> Rectifications { get; init; }
}

public sealed class DetailWriteModel
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

public sealed class VatWriteModel
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

public sealed class TaxKeyWriteModel
{
    public int NumOrden { get; init; }
    public required string ClaveRegimenIva { get; init; }
}

public sealed class RecipientWriteModel
{
    public int NumOrden { get; init; }
    public string? Nif { get; init; }
    public string? CodigoPais { get; init; }
    public string? IdType { get; init; }
    public string? IdOtro { get; init; }
    public string? ApellidosNombre { get; init; }
    public string? CodigoPostal { get; init; }
    public string? Direccion { get; init; }
}

public sealed class RectificationWriteModel
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

public static class TicketSqlMapper
{
    public static TicketWriteModel Map(ParsedTicket ticket, IEnumerable<ConversionWarning>? extraWarnings = null)
    {
        var warnings = extraWarnings?.ToList() ?? [];
        var keys = new List<TaxKeyWriteModel>();
        foreach (var key in ticket.TaxKeys)
        {
            if (key.ClaveRegimenIva is { Length: 2 })
            {
                keys.Add(new TaxKeyWriteModel
                {
                    NumOrden = key.NumOrden,
                    ClaveRegimenIva = key.ClaveRegimenIva
                });
                continue;
            }

            warnings.Add(new ConversionWarning
            {
                Code = "CLAVE_IVA_NO_PERSISTIBLE",
                Field = "ClaveRegimenIvaOpTrascendencia",
                Message = "La clave de IVA no tiene 2 caracteres y no se inserta en TICKET_CLAVE. No se trunca ni se transforma.",
                RawValue = key.ClaveRegimenIva
            });
        }

        return new TicketWriteModel
        {
            Source = ticket,
            Warnings = warnings,
            NifEmisor = Exact(ticket.NifEmisor, 9, "NIF_EMISOR", warnings),
            RazonSocialEmisor = Max(ticket.RazonSocialEmisor, 120, "RAZON_SOCIAL_EMISOR", warnings),
            SerieFactura = Max(ticket.SerieFactura, 20, "SERIE_FACTURA", warnings),
            NumFactura = Max(ticket.NumFactura, 20, "NUM_FACTURA", warnings),
            FechaExpedicion = ticket.FechaExpedicion,
            HoraExpedicion = ticket.HoraExpedicion,
            Tienda = ticket.Tienda,
            Tpv = ticket.Tpv,
            NVendedor = ticket.NVendedor,
            DVendedor = Max(ticket.DVendedor, 40, "D_VENDEDOR", warnings),
            NFormaPago = ticket.NFormaPago,
            DFormaPago = Max(ticket.DFormaPago, 30, "D_FORMA_PAGO", warnings),
            ImporteTotal = ticket.ImporteTotal,
            FacturaSimplificada = ticket.FacturaSimplificada,
            FacturaSustitucionSimplificada = ticket.FacturaSustitucionSimplificada,
            EmitidaPor = Max(ticket.EmitidaPor, 1, "EMITIDA_POR", warnings),
            DescripcionFactura = Max(ticket.DescripcionFactura, 250, "DESCRIPCION_FACTURA", warnings),
            FechaOperacion = ticket.FechaOperacion,
            RetencionSoportada = ticket.RetencionSoportada,
            BaseImponibleACoste = ticket.BaseImponibleACoste,
            IdVersionTbai = Max(ticket.IdVersionTbai, 10, "ID_VERSION_TBAI", warnings),
            NEncargo = ticket.NEncargo,
            IdSala = ticket.IdSala,
            TipoMesa = Max(ticket.TipoMesa, 10, "TIPO_MESA", warnings),
            IdMesa = ticket.IdMesa,
            IdClient = Max(ticket.IdClient, 30, "ID_CLIENT", warnings),
            NumSerieDispositivo = Max(ticket.NumSerieDispositivo, 30, "NUM_SERIE_DISPOSITIVO", warnings),
            SerieFacturaAnterior = Max(ticket.SerieFacturaAnterior, 20, "SERIE_FACTURA_ANTERIOR", warnings),
            NumFacturaAnterior = Max(ticket.NumFacturaAnterior, 20, "NUM_FACTURA_ANTERIOR", warnings),
            FechaFacturaAnterior = ticket.FechaFacturaAnterior,
            HashFirmaFacturaAnterior = Max(ticket.HashFirmaFacturaAnterior, 128, "HASH_FIRMA_FACTURA_ANTERIOR", warnings),
            Details = ticket.Details.Select(d => MapDetail(d, warnings)).ToArray(),
            VatBreakdowns = ticket.VatBreakdowns.Select(v => MapVat(v, warnings)).ToArray(),
            TaxKeys = keys,
            Recipients = ticket.Recipients.Select(r => MapRecipient(r, warnings)).ToArray(),
            Rectifications = ticket.Rectifications.Select(r => MapRectification(r, warnings)).ToArray()
        };
    }

    private static DetailWriteModel MapDetail(ParsedTicketDetail d, List<ConversionWarning> warnings)
        => new()
        {
            NumLinea = d.NumLinea,
            Descripcion = Max(d.Descripcion, 250, "DESCRIPCION", warnings),
            Cantidad = d.Cantidad,
            ImporteUnitario = d.ImporteUnitario,
            Descuento = d.Descuento,
            ImporteTotal = d.ImporteTotal,
            CodigoCentral = Max(d.CodigoCentral, 13, "CODIGO_CENTRAL", warnings),
            Identificador = Max(d.Identificador, 13, "IDENTIFICADOR", warnings),
            Familia = Max(d.Familia, 13, "FAMILIA", warnings),
            Seccion = d.Seccion,
            Formato = d.Formato,
            Esperpes = d.Esperpes,
            SeccionSala = Max(d.SeccionSala, 20, "SECCION_SALA", warnings),
            PvpConsumo = d.PvpConsumo,
            EsKit = d.EsKit,
            IdTiquetlMaster = Max(d.IdTiquetlMaster, 50, "ID_TIQUETL_MASTER", warnings),
            IdTiquetl = Max(d.IdTiquetl, 50, "ID_TIQUETL", warnings),
            EquivalenciaUnidad = d.EquivalenciaUnidad,
            EquivalenciaPeso = d.EquivalenciaPeso,
            PorcentajeIva = d.PorcentajeIva,
            PorcentajeRecargo = d.PorcentajeRecargo
        };

    private static VatWriteModel MapVat(ParsedVatBreakdown v, List<ConversionWarning> warnings)
        => new()
        {
            NumOrden = v.NumOrden,
            TipoDesglose = Max(v.TipoDesglose, 32, "TIPO_DESGLOSE", warnings),
            TipoSujecion = Max(v.TipoSujecion, 16, "TIPO_SUJECION", warnings),
            TipoNoExenta = Max(v.TipoNoExenta, 2, "TIPO_NO_EXENTA", warnings),
            CausaExencion = Max(v.CausaExencion, 2, "CAUSA_EXENCION", warnings),
            CausaNoSujeta = Max(v.CausaNoSujeta, 2, "CAUSA_NO_SUJETA", warnings),
            BaseImponible = v.BaseImponible,
            TipoImpositivo = v.TipoImpositivo,
            CuotaImpuesto = v.CuotaImpuesto,
            TipoRecargoEquivalencia = v.TipoRecargoEquivalencia,
            CuotaRecargoEquivalencia = v.CuotaRecargoEquivalencia,
            OperacionRecargoOSimplificado = v.OperacionRecargoOSimplificado,
            ImporteNoSujeta = v.ImporteNoSujeta
        };

    private static RecipientWriteModel MapRecipient(ParsedRecipient r, List<ConversionWarning> warnings)
        => new()
        {
            NumOrden = r.NumOrden,
            Nif = Exact(r.Nif, 9, "DESTINATARIO_NIF", warnings),
            CodigoPais = Max(r.CodigoPais, 2, "CODIGO_PAIS", warnings),
            IdType = Max(r.IdType, 2, "ID_TYPE", warnings),
            IdOtro = Max(r.IdOtro, 20, "ID_OTRO", warnings),
            ApellidosNombre = Max(r.ApellidosNombre, 120, "APELLIDOS_NOMBRE", warnings),
            CodigoPostal = Max(r.CodigoPostal, 20, "CODIGO_POSTAL", warnings),
            Direccion = Max(r.Direccion, 250, "DIRECCION", warnings)
        };

    private static RectificationWriteModel MapRectification(ParsedRectification r, List<ConversionWarning> warnings)
        => new()
        {
            NumOrden = r.NumOrden,
            Codigo = Max(r.Codigo, 2, "RECTIFICACION_CODIGO", warnings),
            Tipo = Max(r.Tipo, 1, "RECTIFICACION_TIPO", warnings),
            BaseRectificada = r.BaseRectificada,
            CuotaRectificada = r.CuotaRectificada,
            CuotaRecargoRectificada = r.CuotaRecargoRectificada,
            // TICKET_RECTIFICACION no tiene TIENDA/TPV propios: se conserva el
            // SerieFactura XML (p. ej. 52.2.1) para no perder esa información.
            SerieFactura = Max(r.SerieFactura, 20, "RECTIFICACION_SERIE", warnings),
            NumFactura = Max(r.NumFactura, 20, "RECTIFICACION_NUM", warnings),
            FechaExpedicion = r.FechaExpedicion
        };

    private static string? Max(string? value, int max, string field, List<ConversionWarning> warnings)
    {
        if (value is null)
            return null;
        if (value.Length <= max)
            return value;

        warnings.Add(new ConversionWarning
        {
            Code = "LONGITUD_EXCEDIDA",
            Field = field,
            Message = $"El campo {field} tiene {value.Length} caracteres (máximo SQL {max}). No se trunca ni se persiste.",
            RawValue = value
        });
        return null;
    }

    private static string? Exact(string? value, int length, string field, List<ConversionWarning> warnings)
    {
        if (value is null)
            return null;
        if (value.Length == length)
            return value;

        warnings.Add(new ConversionWarning
        {
            Code = "LONGITUD_EXCEDIDA",
            Field = field,
            Message = $"El campo {field} debe tener {length} caracteres y tiene {value.Length}. No se trunca ni se persiste.",
            RawValue = value
        });
        return null;
    }
}
