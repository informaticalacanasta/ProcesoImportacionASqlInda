using System.Xml;
using System.Xml.Linq;
using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class TicketXmlParser
{
    public ParseResult Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new ParseResult
            {
                Success = false,
                Errors = [$"XML mal formado: {ex.Message}"]
            };
        }

        return Parse(document);
    }

    public ParseResult Parse(XDocument document)
    {
        var warnings = new List<ConversionWarning>();
        var unknown = new List<UnknownXmlElement>();
        var conversions = new TicketBaiConversions(warnings);
        var root = document.Root;

        if (root is null || root.Name.LocalName != "TicketBai")
        {
            return new ParseResult
            {
                Success = false,
                Errors = ["La raíz del documento no es TicketBai."],
                Warnings = warnings,
                UnknownElements = unknown
            };
        }

        if (root.Name.NamespaceName != TicketBaiXml.NamespaceUri)
        {
            warnings.Add(new ConversionWarning
            {
                Code = "NAMESPACE_RAIZ",
                Field = "TicketBai",
                Message = $"Namespace de raíz '{root.Name.NamespaceName}' distinto de '{TicketBaiXml.NamespaceUri}'. Se continúa el parseo.",
                RawValue = root.Name.NamespaceName
            });
        }

        ReportUnknown(root, "TicketBai", ["Cabecera", "Sujetos", "Factura", "HuellaTBAI", "Signature"], unknown);

        var cabecera = root.Child("Cabecera");
        var sujetos = root.Child("Sujetos");
        var factura = root.Child("Factura");
        var huella = root.Child("HuellaTBAI");

        var details = new List<ParsedTicketDetail>();
        var vat = new List<ParsedVatBreakdown>();
        var keys = new List<ParsedTaxKey>();
        var recipients = new List<ParsedRecipient>();
        var rectifications = new List<ParsedRectification>();

        string? idVersion = null;
        int? nFormaPago = null;
        string? dFormaPago = null;
        int? nVendedor = null;
        string? dVendedor = null;
        long? nEncargo = null;
        int? idSala = null;
        string? tipoMesa = null;
        int? idMesa = null;
        string? idClient = null;

        if (cabecera is not null)
        {
            ReportUnknown(
                cabecera,
                "Cabecera",
                [
                    "IDVersionTBAI", "code_DirE", "name_DirE", "role_Buyer_DirE", "type_DirE",
                    "email_DirE", "enviarEmail_DirE", "estado_DirE", "NFpago", "DFpago",
                    "NVendedor", "DVendedor", "NEncargo", "Id_Sala", "Tipo_mesa", "Id_Mesa", "Id_Client"
                ],
                unknown);
            idVersion = conversions.Text(cabecera.ChildText("IDVersionTBAI"));
            nFormaPago = conversions.Int32(cabecera.ChildText("NFpago"), "NFpago");
            dFormaPago = conversions.Text(cabecera.ChildText("DFpago"));
            nVendedor = conversions.Int32(cabecera.ChildText("NVendedor"), "NVendedor");
            dVendedor = conversions.Text(cabecera.ChildText("DVendedor"));
            nEncargo = conversions.Int64(cabecera.ChildText("NEncargo"), "NEncargo");
            idSala = conversions.Int32(cabecera.ChildText("Id_Sala"), "Id_Sala");
            tipoMesa = conversions.Text(cabecera.ChildText("Tipo_mesa"));
            idMesa = conversions.Int32(cabecera.ChildText("Id_Mesa"), "Id_Mesa");
            idClient = conversions.Text(cabecera.ChildText("Id_Client"));
        }

        string? nifEmisor = null;
        string? razonSocial = null;
        string? emitidaPor = null;
        if (sujetos is not null)
        {
            ReportUnknown(sujetos, "Sujetos", ["Emisor", "Destinatarios", "VariosDestinatarios", "EmitidaPorTercerosODestinatario"], unknown);
            var emisor = sujetos.Child("Emisor");
            if (emisor is not null)
            {
                ReportUnknown(emisor, "Sujetos/Emisor", ["NIF", "ApellidosNombreRazonSocial"], unknown);
                nifEmisor = conversions.Text(emisor.ChildText("NIF"));
                razonSocial = conversions.Text(emisor.ChildText("ApellidosNombreRazonSocial"));
            }

            emitidaPor = conversions.Text(sujetos.ChildText("EmitidaPorTercerosODestinatario"));
            ParseRecipients(sujetos.Child("Destinatarios"), conversions, recipients, unknown);
        }

        string? serie = null;
        string? numFactura = null;
        DateOnly? fecha = null;
        TimeOnly? hora = null;
        bool? simplificada = null;
        bool? sustitucion = null;
        string? descripcion = null;
        DateOnly? fechaOperacion = null;
        decimal? importeTotal = null;
        decimal? retencion = null;
        decimal? baseCoste = null;

        if (factura is not null)
        {
            ReportUnknown(factura, "Factura", ["CabeceraFactura", "DatosFactura", "TipoDesglose"], unknown);
            var cabFac = factura.Child("CabeceraFactura");
            if (cabFac is not null)
            {
                ReportUnknown(
                    cabFac,
                    "Factura/CabeceraFactura",
                    [
                        "SerieFactura", "NumFactura", "FechaExpedicionFactura", "HoraExpedicionFactura",
                        "FacturaSimplificada", "FacturaEmitidaSustitucionSimplificada",
                        "FacturaRectificativa", "FacturasRectificadasSustituidas"
                    ],
                    unknown);
                serie = conversions.Text(cabFac.ChildText("SerieFactura"));
                numFactura = conversions.Text(cabFac.ChildText("NumFactura"));
                fecha = conversions.Date(cabFac.ChildText("FechaExpedicionFactura"), "FechaExpedicionFactura");
                hora = conversions.Time(cabFac.ChildText("HoraExpedicionFactura"), "HoraExpedicionFactura");
                simplificada = conversions.SiNo(cabFac.ChildText("FacturaSimplificada"), "FacturaSimplificada");
                sustitucion = conversions.SiNo(cabFac.ChildText("FacturaEmitidaSustitucionSimplificada"), "FacturaEmitidaSustitucionSimplificada");
                ParseRectifications(cabFac, conversions, rectifications, unknown);
            }

            var datos = factura.Child("DatosFactura");
            if (datos is not null)
            {
                ReportUnknown(
                    datos,
                    "Factura/DatosFactura",
                    [
                        "FechaOperacion", "DescripcionFactura", "DetallesFactura", "ImporteTotalFactura",
                        "RetencionSoportada", "BaseImponibleACoste", "Claves"
                    ],
                    unknown);
                fechaOperacion = conversions.Date(datos.ChildText("FechaOperacion"), "FechaOperacion");
                descripcion = conversions.Text(datos.ChildText("DescripcionFactura"));
                importeTotal = conversions.Decimal(datos.ChildText("ImporteTotalFactura"), "ImporteTotalFactura");
                retencion = conversions.Decimal(datos.ChildText("RetencionSoportada"), "RetencionSoportada");
                baseCoste = conversions.Decimal(datos.ChildText("BaseImponibleACoste"), "BaseImponibleACoste");
                ParseDetails(datos.Child("DetallesFactura"), conversions, details, unknown);
                ParseTaxKeys(datos.Child("Claves"), conversions, keys, unknown);
            }

            ParseTipoDesglose(factura.Child("TipoDesglose"), conversions, vat, unknown);
        }

        string? numSerieDispositivo = null;
        string? serieFacturaAnterior = null;
        string? numFacturaAnterior = null;
        DateOnly? fechaFacturaAnterior = null;
        string? hashFirmaFacturaAnterior = null;
        if (huella is not null)
        {
            ReportUnknown(huella, "HuellaTBAI", ["EncadenamientoFacturaAnterior", "Software", "NumSerieDispositivo"], unknown);
            numSerieDispositivo = conversions.Text(huella.ChildText("NumSerieDispositivo"));
            var encadenamiento = huella.Child("EncadenamientoFacturaAnterior");
            if (encadenamiento is not null)
            {
                ReportUnknown(
                    encadenamiento,
                    "HuellaTBAI/EncadenamientoFacturaAnterior",
                    [
                        "SerieFacturaAnterior", "NumFacturaAnterior",
                        "FechaExpedicionFacturaAnterior", "SignatureValueFirmaFacturaAnterior"
                    ],
                    unknown);
                serieFacturaAnterior = conversions.Text(encadenamiento.ChildText("SerieFacturaAnterior"));
                numFacturaAnterior = conversions.Text(encadenamiento.ChildText("NumFacturaAnterior"));
                fechaFacturaAnterior = conversions.Date(
                    encadenamiento.ChildText("FechaExpedicionFacturaAnterior"),
                    "FechaExpedicionFacturaAnterior");
                hashFirmaFacturaAnterior = conversions.Text(encadenamiento.ChildText("SignatureValueFirmaFacturaAnterior"));
            }
        }

        var partesSerie = SerieFacturaExtractor.Parse(serie);

        var ticket = new ParsedTicket
        {
            IdVersionTbai = idVersion,
            NifEmisor = nifEmisor,
            RazonSocialEmisor = razonSocial,
            SerieFactura = partesSerie.SerieDocumento,
            NumFactura = numFactura,
            FechaExpedicion = fecha,
            HoraExpedicion = hora,
            Tienda = partesSerie.Tienda,
            Tpv = partesSerie.Tpv,
            NVendedor = nVendedor,
            DVendedor = dVendedor,
            NFormaPago = nFormaPago,
            DFormaPago = dFormaPago,
            ImporteTotal = importeTotal,
            FacturaSimplificada = simplificada,
            FacturaSustitucionSimplificada = sustitucion,
            EmitidaPor = emitidaPor,
            DescripcionFactura = descripcion,
            FechaOperacion = fechaOperacion,
            RetencionSoportada = retencion,
            BaseImponibleACoste = baseCoste,
            NEncargo = nEncargo,
            IdSala = idSala,
            TipoMesa = tipoMesa,
            IdMesa = idMesa,
            IdClient = idClient,
            NumSerieDispositivo = numSerieDispositivo,
            SerieFacturaAnterior = serieFacturaAnterior,
            NumFacturaAnterior = numFacturaAnterior,
            FechaFacturaAnterior = fechaFacturaAnterior,
            HashFirmaFacturaAnterior = hashFirmaFacturaAnterior,
            Details = details,
            VatBreakdowns = vat,
            TaxKeys = keys,
            Recipients = recipients,
            Rectifications = rectifications
        };

        return new ParseResult
        {
            Success = true,
            Ticket = ticket,
            Warnings = warnings,
            UnknownElements = unknown
        };
    }

    private static void ParseDetails(
        XElement? detalles,
        TicketBaiConversions conversions,
        List<ParsedTicketDetail> details,
        List<UnknownXmlElement> unknown)
    {
        if (detalles is null)
            return;

        ReportUnknown(detalles, "DetallesFactura", ["IDDetalleFactura"], unknown);
        var linea = 1;
        foreach (var detalle in detalles.Children("IDDetalleFactura"))
        {
            ReportUnknown(
                detalle,
                "IDDetalleFactura",
                [
                    "DescripcionDetalle", "Cantidad", "ImporteUnitario", "Descuento", "ImporteTotal",
                    "CodigoCentral", "Identificador", "Familia", "Seccion", "Formato", "Esperpes",
                    "SeccionSala", "PvpConsumo", "PVPConsumo", "EsKit", "Id_tiquetlmaster", "Id_tiquetl",
                    "Equivalencia_Unidad", "Equivalencia_Peso", "PorcentajeIva", "PorcentajeRecargo"
                ],
                unknown);

            var pvpNode = detalle.Child("PvpConsumo") ?? detalle.Child("PVPConsumo");

            details.Add(new ParsedTicketDetail
            {
                NumLinea = linea++,
                Descripcion = conversions.Text(detalle.ChildText("DescripcionDetalle")),
                Cantidad = conversions.Decimal(detalle.ChildText("Cantidad"), "Cantidad"),
                ImporteUnitario = conversions.Decimal(detalle.ChildText("ImporteUnitario"), "ImporteUnitario"),
                Descuento = conversions.Decimal(detalle.ChildText("Descuento"), "Descuento"),
                ImporteTotal = conversions.Decimal(detalle.ChildText("ImporteTotal"), "ImporteTotal"),
                CodigoCentral = conversions.Text(detalle.ChildText("CodigoCentral")),
                Identificador = conversions.Text(detalle.ChildText("Identificador")),
                Familia = conversions.Text(detalle.ChildText("Familia")),
                Seccion = conversions.Int32(detalle.ChildText("Seccion"), "Seccion"),
                Formato = conversions.Int32(detalle.ChildText("Formato"), "Formato"),
                Esperpes = conversions.SiNo(detalle.ChildText("Esperpes"), "Esperpes"),
                SeccionSala = conversions.Text(detalle.ChildText("SeccionSala")),
                PvpConsumo = conversions.Decimal(pvpNode?.Value, "PvpConsumo"),
                EsKit = conversions.SiNo(detalle.ChildText("EsKit"), "EsKit"),
                IdTiquetlMaster = conversions.Text(detalle.ChildText("Id_tiquetlmaster")),
                IdTiquetl = conversions.Text(detalle.ChildText("Id_tiquetl")),
                EquivalenciaUnidad = conversions.Decimal(detalle.ChildText("Equivalencia_Unidad"), "Equivalencia_Unidad"),
                EquivalenciaPeso = conversions.Decimal(detalle.ChildText("Equivalencia_Peso"), "Equivalencia_Peso"),
                PorcentajeIva = conversions.Decimal(detalle.ChildText("PorcentajeIva"), "PorcentajeIva"),
                PorcentajeRecargo = conversions.Decimal(detalle.ChildText("PorcentajeRecargo"), "PorcentajeRecargo")
            });
        }
    }

    private static void ParseTaxKeys(
        XElement? claves,
        TicketBaiConversions conversions,
        List<ParsedTaxKey> keys,
        List<UnknownXmlElement> unknown)
    {
        if (claves is null)
            return;

        ReportUnknown(claves, "Claves", ["IDClave"], unknown);
        var orden = 1;
        foreach (var idClave in claves.Children("IDClave"))
        {
            ReportUnknown(idClave, "IDClave", ["ClaveRegimenIvaOpTrascendencia"], unknown);
            var clave = conversions.Text(idClave.ChildText("ClaveRegimenIvaOpTrascendencia"));
            if (clave is not null && clave.Length != 2)
            {
                conversions.WarnLength("ClaveRegimenIvaOpTrascendencia", clave, 2);
            }

            keys.Add(new ParsedTaxKey
            {
                NumOrden = orden++,
                ClaveRegimenIva = clave
            });
        }
    }

    private static void ParseRecipients(
        XElement? destinatarios,
        TicketBaiConversions conversions,
        List<ParsedRecipient> recipients,
        List<UnknownXmlElement> unknown)
    {
        if (destinatarios is null)
            return;

        ReportUnknown(destinatarios, "Destinatarios", ["IDDestinatario"], unknown);
        var orden = 1;
        foreach (var dest in destinatarios.Children("IDDestinatario"))
        {
            ReportUnknown(
                dest,
                "IDDestinatario",
                ["NIF", "IDOtro", "ApellidosNombreRazonSocial", "CodigoPostal", "Direccion"],
                unknown);
            var idOtro = dest.Child("IDOtro");
            string? codigoPais = null;
            string? idType = null;
            string? id = null;
            if (idOtro is not null)
            {
                ReportUnknown(idOtro, "IDOtro", ["CodigoPais", "IDType", "ID"], unknown);
                codigoPais = conversions.Text(idOtro.ChildText("CodigoPais"));
                idType = conversions.Text(idOtro.ChildText("IDType"));
                id = conversions.Text(idOtro.ChildText("ID"));
            }

            recipients.Add(new ParsedRecipient
            {
                NumOrden = orden++,
                Nif = conversions.Text(dest.ChildText("NIF")),
                CodigoPais = codigoPais,
                IdType = idType,
                IdOtro = id,
                ApellidosNombre = conversions.Text(dest.ChildText("ApellidosNombreRazonSocial")),
                CodigoPostal = conversions.Text(dest.ChildText("CodigoPostal")),
                Direccion = conversions.Text(dest.ChildText("Direccion"))
            });
        }
    }

    private static void ParseRectifications(
        XElement cabFac,
        TicketBaiConversions conversions,
        List<ParsedRectification> rectifications,
        List<UnknownXmlElement> unknown)
    {
        var rect = cabFac.Child("FacturaRectificativa");
        var refs = cabFac.Child("FacturasRectificadasSustituidas");
        if (rect is null && refs is null)
            return;

        string? codigo = null;
        string? tipo = null;
        decimal? baseRect = null;
        decimal? cuota = null;
        decimal? cuotaRecargo = null;
        if (rect is not null)
        {
            ReportUnknown(rect, "FacturaRectificativa", ["Codigo", "Tipo", "ImporteRectificacionSustitutiva"], unknown);
            codigo = conversions.Text(rect.ChildText("Codigo"));
            tipo = conversions.Text(rect.ChildText("Tipo"));
            var importe = rect.Child("ImporteRectificacionSustitutiva");
            if (importe is not null)
            {
                ReportUnknown(importe, "ImporteRectificacionSustitutiva", ["BaseRectificada", "CuotaRectificada", "CuotaRecargoRectificada"], unknown);
                baseRect = conversions.Decimal(importe.ChildText("BaseRectificada"), "BaseRectificada");
                cuota = conversions.Decimal(importe.ChildText("CuotaRectificada"), "CuotaRectificada");
                cuotaRecargo = conversions.Decimal(importe.ChildText("CuotaRecargoRectificada"), "CuotaRecargoRectificada");
            }
        }

        if (refs is not null)
        {
            ReportUnknown(refs, "FacturasRectificadasSustituidas", ["IDFacturaRectificadaSustituida"], unknown);
            var orden = 1;
            foreach (var id in refs.Children("IDFacturaRectificadaSustituida"))
            {
                ReportUnknown(id, "IDFacturaRectificadaSustituida", ["SerieFactura", "NumFactura", "FechaExpedicionFactura"], unknown);
                rectifications.Add(new ParsedRectification
                {
                    NumOrden = orden++,
                    Codigo = codigo,
                    Tipo = tipo,
                    BaseRectificada = baseRect,
                    CuotaRectificada = cuota,
                    CuotaRecargoRectificada = cuotaRecargo,
                    SerieFactura = conversions.Text(id.ChildText("SerieFactura")),
                    NumFactura = conversions.Text(id.ChildText("NumFactura")),
                    FechaExpedicion = conversions.Date(id.ChildText("FechaExpedicionFactura"), "FechaExpedicionFacturaRectificada")
                });
            }

            return;
        }

        rectifications.Add(new ParsedRectification
        {
            NumOrden = 1,
            Codigo = codigo,
            Tipo = tipo,
            BaseRectificada = baseRect,
            CuotaRectificada = cuota,
            CuotaRecargoRectificada = cuotaRecargo
        });
    }

    private static void ParseTipoDesglose(
        XElement? tipoDesglose,
        TicketBaiConversions conversions,
        List<ParsedVatBreakdown> vat,
        List<UnknownXmlElement> unknown)
    {
        if (tipoDesglose is null)
            return;

        ReportUnknown(tipoDesglose, "TipoDesglose", ["DesgloseFactura", "DesgloseTipoOperacion"], unknown);
        var desgloseFactura = tipoDesglose.Child("DesgloseFactura");
        if (desgloseFactura is not null)
        {
            ParseSujetaNoSujeta(desgloseFactura, "DesgloseFactura", conversions, vat, unknown);
            return;
        }

        var tipoOp = tipoDesglose.Child("DesgloseTipoOperacion");
        if (tipoOp is null)
            return;

        ReportUnknown(tipoOp, "DesgloseTipoOperacion", ["PrestacionServicios", "Entrega"], unknown);
        var prestacion = tipoOp.Child("PrestacionServicios");
        if (prestacion is not null)
            ParseSujetaNoSujeta(prestacion, "PrestacionServicios", conversions, vat, unknown);
        var entrega = tipoOp.Child("Entrega");
        if (entrega is not null)
            ParseSujetaNoSujeta(entrega, "Entrega", conversions, vat, unknown);
    }

    private static void ParseSujetaNoSujeta(
        XElement parent,
        string tipoDesglose,
        TicketBaiConversions conversions,
        List<ParsedVatBreakdown> vat,
        List<UnknownXmlElement> unknown)
    {
        ReportUnknown(parent, tipoDesglose, ["Sujeta", "NoSujeta"], unknown);
        var sujeta = parent.Child("Sujeta");
        if (sujeta is not null)
        {
            ReportUnknown(sujeta, "Sujeta", ["Exenta", "NoExenta"], unknown);
            var noExenta = sujeta.Child("NoExenta");
            if (noExenta is not null)
            {
                ReportUnknown(noExenta, "NoExenta", ["DetalleNoExenta"], unknown);
                foreach (var detalle in noExenta.Children("DetalleNoExenta"))
                {
                    ReportUnknown(detalle, "DetalleNoExenta", ["TipoNoExenta", "DesgloseIVA"], unknown);
                    var tipoNoExenta = conversions.Text(detalle.ChildText("TipoNoExenta"));
                    var desgloseIva = detalle.Child("DesgloseIVA");
                    if (desgloseIva is null)
                        continue;

                    ReportUnknown(desgloseIva, "DesgloseIVA", ["DetalleIVA"], unknown);
                    foreach (var iva in desgloseIva.Children("DetalleIVA"))
                    {
                        ReportUnknown(
                            iva,
                            "DetalleIVA",
                            [
                                "BaseImponible", "TipoImpositivo", "CuotaImpuesto",
                                "TipoRecargoEquivalencia", "CuotaRecargoEquivalencia",
                                "OperacionEnRecargoDeEquivalenciaORegimenSimplificado"
                            ],
                            unknown);
                        vat.Add(new ParsedVatBreakdown
                        {
                            NumOrden = vat.Count + 1,
                            TipoDesglose = tipoDesglose,
                            TipoSujecion = "NoExenta",
                            TipoNoExenta = tipoNoExenta,
                            BaseImponible = conversions.Decimal(iva.ChildText("BaseImponible"), "BaseImponible"),
                            TipoImpositivo = conversions.Decimal(iva.ChildText("TipoImpositivo"), "TipoImpositivo"),
                            CuotaImpuesto = conversions.Decimal(iva.ChildText("CuotaImpuesto"), "CuotaImpuesto"),
                            TipoRecargoEquivalencia = conversions.Decimal(iva.ChildText("TipoRecargoEquivalencia"), "TipoRecargoEquivalencia"),
                            CuotaRecargoEquivalencia = conversions.Decimal(iva.ChildText("CuotaRecargoEquivalencia"), "CuotaRecargoEquivalencia"),
                            OperacionRecargoOSimplificado = conversions.SiNo(
                                iva.ChildText("OperacionEnRecargoDeEquivalenciaORegimenSimplificado"),
                                "OperacionEnRecargoDeEquivalenciaORegimenSimplificado")
                        });
                    }
                }
            }

            var exenta = sujeta.Child("Exenta");
            if (exenta is not null)
            {
                ReportUnknown(exenta, "Exenta", ["DetalleExenta"], unknown);
                foreach (var detalle in exenta.Children("DetalleExenta"))
                {
                    ReportUnknown(detalle, "DetalleExenta", ["CausaExencion", "BaseImponible"], unknown);
                    vat.Add(new ParsedVatBreakdown
                    {
                        NumOrden = vat.Count + 1,
                        TipoDesglose = tipoDesglose,
                        TipoSujecion = "Exenta",
                        CausaExencion = conversions.Text(detalle.ChildText("CausaExencion")),
                        BaseImponible = conversions.Decimal(detalle.ChildText("BaseImponible"), "BaseImponible")
                    });
                }
            }
        }

        var noSujeta = parent.Child("NoSujeta");
        if (noSujeta is not null)
        {
            ReportUnknown(noSujeta, "NoSujeta", ["DetalleNoSujeta"], unknown);
            foreach (var detalle in noSujeta.Children("DetalleNoSujeta"))
            {
                ReportUnknown(detalle, "DetalleNoSujeta", ["Causa", "Importe"], unknown);
                vat.Add(new ParsedVatBreakdown
                {
                    NumOrden = vat.Count + 1,
                    TipoDesglose = tipoDesglose,
                    TipoSujecion = "NoSujeta",
                    CausaNoSujeta = conversions.Text(detalle.ChildText("Causa")),
                    ImporteNoSujeta = conversions.Decimal(detalle.ChildText("Importe"), "ImporteNoSujeta")
                });
            }
        }
    }

    private static void ReportUnknown(
        XElement parent,
        string parentPath,
        IReadOnlyCollection<string> expected,
        List<UnknownXmlElement> unknown)
    {
        foreach (var child in parent.Elements())
        {
            if (expected.Contains(child.Name.LocalName))
                continue;

            unknown.Add(new UnknownXmlElement
            {
                ParentPath = parentPath,
                LocalName = child.Name.LocalName
            });
        }
    }
}
