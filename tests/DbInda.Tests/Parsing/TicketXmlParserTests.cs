using DbInda.Worker.Parsing;

namespace DbInda.Tests.Parsing;

public sealed class TicketXmlParserTests
{
    private readonly TicketXmlParser _parser = new();
    private readonly TicketDocumentReader _reader = new();

    [Fact]
    public void Xml_real_normal_se_parsea()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Ticket);
        Assert.Equal("B29189644", result.Ticket.NifEmisor);
        Assert.Equal("1", result.Ticket.SerieFactura);
        Assert.Equal("1", result.Ticket.NumFactura);
        Assert.Equal(new DateOnly(2026, 8, 15), result.Ticket.FechaExpedicion);
        Assert.Equal(new TimeOnly(10, 7, 59), result.Ticket.HoraExpedicion);
        Assert.Equal(52, result.Ticket.Tienda);
        Assert.Equal(2, result.Ticket.Tpv);
        Assert.Equal(3.00m, result.Ticket.ImporteTotal);
        Assert.True(result.Ticket.FacturaSimplificada);
        Assert.Equal("N", result.Ticket.EmitidaPor);
        Assert.Single(result.Ticket.Details);
        Assert.Single(result.Ticket.VatBreakdowns);
        Assert.Single(result.Ticket.TaxKeys);
        Assert.Empty(result.Ticket.Recipients);
        Assert.Empty(result.Ticket.Rectifications);
        Assert.Equal("644522250725084342", result.Ticket.NumSerieDispositivo);
        Assert.Null(result.Ticket.SerieFacturaAnterior);
        Assert.Null(result.Ticket.NumFacturaAnterior);
        Assert.Null(result.Ticket.FechaFacturaAnterior);
        Assert.Null(result.Ticket.HashFirmaFacturaAnterior);
    }

    [Fact]
    public void Encadenamiento_factura_anterior_completo()
    {
        var xml = TicketBaiSkeleton.WrapFactura().Replace(
            "<Software>",
            """
            <EncadenamientoFacturaAnterior>
            <SerieFacturaAnterior>66.1.1</SerieFacturaAnterior>
            <NumFacturaAnterior>42382</NumFacturaAnterior>
            <FechaExpedicionFacturaAnterior>27-07-2026</FechaExpedicionFacturaAnterior>
            <SignatureValueFirmaFacturaAnterior>b6ba6f08e8c3aa99d57bb757dde059db98690465e4ef636bc35af925a6d31b8c</SignatureValueFirmaFacturaAnterior>
            </EncadenamientoFacturaAnterior>
            <Software>
            """);
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal("66.1.1", result.Ticket!.SerieFacturaAnterior);
        Assert.Equal("42382", result.Ticket.NumFacturaAnterior);
        Assert.Equal(new DateOnly(2026, 7, 27), result.Ticket.FechaFacturaAnterior);
        Assert.Equal("b6ba6f08e8c3aa99d57bb757dde059db98690465e4ef636bc35af925a6d31b8c", result.Ticket.HashFirmaFacturaAnterior);
        Assert.Equal("1", result.Ticket.SerieFactura);
        Assert.Equal(52, result.Ticket.Tienda);
        Assert.Equal(2, result.Ticket.Tpv);
    }

    [Fact]
    public void Sin_encadenamiento_factura_anterior_queda_null()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Ticket);
        Assert.Null(result.Ticket.SerieFacturaAnterior);
        Assert.Null(result.Ticket.NumFacturaAnterior);
        Assert.Null(result.Ticket.FechaFacturaAnterior);
        Assert.Null(result.Ticket.HashFirmaFacturaAnterior);
        Assert.Equal("1", result.Ticket.NumFactura);
    }

    [Fact]
    public void Serie_xml_simple_no_inventa_tienda_ni_tpv()
    {
        var xml = TicketBaiSkeleton.WrapFactura()
            .Replace("<SerieFactura>52.2.1</SerieFactura>", "<SerieFactura>1</SerieFactura>");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal("1", result.Ticket!.SerieFactura);
        Assert.Null(result.Ticket.Tienda);
        Assert.Null(result.Ticket.Tpv);
        Assert.Equal("1", result.Ticket.NumFactura);
    }

    [Fact]
    public void Serie_xml_no_reconocible_se_conserva_integra()
    {
        var xml = TicketBaiSkeleton.WrapFactura()
            .Replace("<SerieFactura>52.2.1</SerieFactura>", "<SerieFactura>AB.CD</SerieFactura>");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal("AB.CD", result.Ticket!.SerieFactura);
        Assert.Null(result.Ticket.Tienda);
        Assert.Null(result.Ticket.Tpv);
    }

    [Fact]
    public void Xml_con_multiples_detalles()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-430_20260815_234606_10.50_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal(4, result.Ticket!.Details.Count);
        Assert.Equal("GALLETAS DE FERIA(T)", result.Ticket.Details[0].Descripcion);
        Assert.Equal(10.50m, result.Ticket.ImporteTotal);
    }

    [Fact]
    public void Xml_con_multiples_bloques_iva()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-430_20260815_234606_10.50_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal(2, result.Ticket!.VatBreakdowns.Count);
        Assert.Contains(result.Ticket.VatBreakdowns, v => v.TipoImpositivo == 0.00m);
        Assert.Contains(result.Ticket.VatBreakdowns, v => v.TipoImpositivo == 10.00m);
        Assert.All(result.Ticket.VatBreakdowns, v => Assert.Equal("NoExenta", v.TipoSujecion));
        Assert.All(result.Ticket.VatBreakdowns, v => Assert.Equal("S1", v.TipoNoExenta));
    }

    [Fact]
    public void Xml_con_multiples_claves()
    {
        var xml = TicketBaiSkeleton.WrapFactura(claves: """
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>01</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>51</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            """);
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal(2, result.Ticket!.TaxKeys.Count);
        Assert.Equal("01", result.Ticket.TaxKeys[0].ClaveRegimenIva);
        Assert.Equal("51", result.Ticket.TaxKeys[1].ClaveRegimenIva);
        Assert.Equal(1, result.Ticket.TaxKeys[0].NumOrden);
        Assert.Equal(2, result.Ticket.TaxKeys[1].NumOrden);
    }

    [Fact]
    public void Ceros_reales_no_son_null_ni_warning()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Equal(0m, result.Ticket!.Details[0].ImporteUnitario);
        Assert.Equal(0L, result.Ticket.NEncargo);
        Assert.Equal(0, result.Ticket.IdSala);
        Assert.Equal(0, result.Ticket.IdMesa);
        Assert.Equal("0", result.Ticket.IdClient);
        Assert.DoesNotContain(result.Warnings, w => w.Field == "ImporteUnitario");
        Assert.DoesNotContain(result.Warnings, w => w.Code == "VALOR_NO_CONVERTIBLE");
    }

    [Fact]
    public void Campo_opcional_ausente_queda_null()
    {
        var xml = TicketBaiSkeleton.WrapFactura();
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Null(result.Ticket!.NVendedor);
        Assert.Null(result.Ticket.DVendedor);
        Assert.Null(result.Ticket.Details[0].CodigoCentral);
        Assert.Null(result.Ticket.FacturaSimplificada);
    }

    [Fact]
    public void Decimal_invalido_queda_null_con_warning()
    {
        var xml = TicketBaiSkeleton.WrapFactura(cantidad: "ABC");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Null(result.Ticket!.Details[0].Cantidad);
        Assert.Contains(result.Warnings, w => w.Field == "Cantidad" && w.Code == "VALOR_NO_CONVERTIBLE");
        Assert.Equal(0m, result.Ticket.Details[0].ImporteUnitario);
    }

    [Fact]
    public void Fecha_invalida_queda_null_con_warning()
    {
        var xml = TicketBaiSkeleton.WrapFactura(fecha: "2026-08-15");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.Null(result.Ticket!.FechaExpedicion);
        Assert.Contains(result.Warnings, w => w.Field == "FechaExpedicionFactura");
    }

    [Fact]
    public void Xml_mal_formado_no_se_parsea()
    {
        var result = _parser.Parse("<T:TicketBai><Cabecera>");
        Assert.False(result.Success);
        Assert.Null(result.Ticket);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Xml_sin_signature_se_parsea()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-72_20260815_162545_3.50_sin_firmar.xml");
        var result = _parser.Parse(xml);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.UnknownElements, e => e.LocalName == "Signature");
        Assert.Equal(21.00m, result.Ticket!.VatBreakdowns[0].TipoImpositivo);
        Assert.Equal(21.00m, result.Ticket.Details[0].PorcentajeIva);
    }

    [Fact]
    public void PvpConsumo_real_se_lee()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _parser.Parse(xml);
        Assert.Equal(0m, result.Ticket!.Details[0].PvpConsumo);
    }

    [Fact]
    public void PVPConsumo_xsd_tambien_se_lee()
    {
        var xml = TicketBaiSkeleton.WrapFactura(detalles: """
            <IDDetalleFactura>
            <DescripcionDetalle>PRUEBA</DescripcionDetalle>
            <Cantidad>1.000</Cantidad>
            <ImporteUnitario>1.00</ImporteUnitario>
            <ImporteTotal>1.00</ImporteTotal>
            <PVPConsumo>2.50</PVPConsumo>
            </IDDetalleFactura>
            """);
        var result = _parser.Parse(xml);
        Assert.Equal(2.50m, result.Ticket!.Details[0].PvpConsumo);
    }

    [Fact]
    public void Destinatario_sintetico()
    {
        var xml = TicketBaiSkeleton.WrapFactura(extraSujetos: """
            <Destinatarios>
            <IDDestinatario>
            <NIF>12345678Z</NIF>
            <ApellidosNombreRazonSocial>CLIENTE PRUEBA</ApellidosNombreRazonSocial>
            <CodigoPostal>48001</CodigoPostal>
            <Direccion>Calle Mayor 1</Direccion>
            </IDDestinatario>
            </Destinatarios>
            """);
        var result = _parser.Parse(xml);
        Assert.True(result.Success);
        var dest = Assert.Single(result.Ticket!.Recipients);
        Assert.Equal("12345678Z", dest.Nif);
        Assert.Equal("CLIENTE PRUEBA", dest.ApellidosNombre);
        Assert.Equal("48001", dest.CodigoPostal);
        Assert.Equal("Calle Mayor 1", dest.Direccion);
    }

    [Fact]
    public void Rectificativa_sintetica()
    {
        var xml = TicketBaiSkeleton.WrapFactura(extraCabeceraFactura: """
            <FacturaRectificativa>
            <Codigo>R1</Codigo>
            <Tipo>I</Tipo>
            <ImporteRectificacionSustitutiva>
            <BaseRectificada>10.00</BaseRectificada>
            <CuotaRectificada>1.00</CuotaRectificada>
            </ImporteRectificacionSustitutiva>
            </FacturaRectificativa>
            <FacturasRectificadasSustituidas>
            <IDFacturaRectificadaSustituida>
            <SerieFactura>52.2.1</SerieFactura>
            <NumFactura>10</NumFactura>
            <FechaExpedicionFactura>14-08-2026</FechaExpedicionFactura>
            </IDFacturaRectificadaSustituida>
            </FacturasRectificadasSustituidas>
            """);
        var result = _parser.Parse(xml);
        var rect = Assert.Single(result.Ticket!.Rectifications);
        Assert.Equal("R1", rect.Codigo);
        Assert.Equal("I", rect.Tipo);
        Assert.Equal(10.00m, rect.BaseRectificada);
        Assert.Equal("10", rect.NumFactura);
        Assert.Equal("52.2.1", rect.SerieFactura);
        Assert.Equal(new DateOnly(2026, 8, 14), rect.FechaExpedicion);
    }

    [Fact]
    public void Filename_correcto()
    {
        var parsed = new TicketFileNameParser().Parse(
            "Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        Assert.True(parsed.PatternMatched);
        Assert.Equal("B29189644", parsed.NifEmisor);
        Assert.Equal("1", parsed.TokenIntermedio);
        Assert.Equal(52, parsed.Tienda);
        Assert.Equal(2, parsed.Tpv);
        Assert.Equal("1", parsed.NumFactura);
        Assert.Equal(new DateOnly(2026, 8, 15), parsed.Fecha);
        Assert.Equal(new TimeOnly(10, 7, 59), parsed.Hora);
        Assert.Equal(3.00m, parsed.Importe);
        Assert.Empty(parsed.Warnings);
    }

    [Fact]
    public void Filename_invalido_no_rechaza_xml()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _reader.Read(xml, "nombre raro.xml");

        Assert.True(result.Success);
        Assert.NotNull(result.Ticket);
        Assert.False(result.FileName!.PatternMatched);
        Assert.Contains(result.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Discrepancia_filename_xml_usa_xml()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _reader.Read(xml, "Fact_B29189644_1_99-9-999_20200101_000000_1.00_sin_firmar.xml");

        Assert.True(result.Success);
        Assert.Equal("1", result.Ticket!.NumFactura);
        Assert.Equal(52, result.Ticket.Tienda);
        Assert.Contains(result.Warnings, w => w.Code == "DISCREPANCIA_FILENAME_XML" && w.Field == "NumFactura");
        Assert.Contains(result.Warnings, w => w.Code == "DISCREPANCIA_FILENAME_XML" && w.Field == "Tienda");
    }

    [Fact]
    public void EmitidaPor_no_es_booleano()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = _parser.Parse(xml);
        Assert.Equal("N", result.Ticket!.EmitidaPor);
    }
}
