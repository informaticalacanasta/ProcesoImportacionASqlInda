using DbInda.Worker.Models;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Processing;

namespace DbInda.Tests.Persistence;

public sealed class TicketSqlMapperTests
{
    [Fact]
    public void Clave_invalida_no_se_mapea_y_queda_en_warning()
    {
        var xml = TicketBaiSkeleton.WrapFactura(claves: """
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>01</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>ABC</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            """);
        var parsed = new TicketXmlParser().Parse(xml);
        var mapped = TicketSqlMapper.Map(parsed.Ticket!);

        var key = Assert.Single(mapped.TaxKeys);
        Assert.Equal("01", key.ClaveRegimenIva);
        Assert.Contains(mapped.Warnings, w => w.Code == "CLAVE_IVA_NO_PERSISTIBLE" && w.RawValue == "ABC");
    }

    [Fact]
    public void Ceros_reales_se_conservan()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var parsed = new TicketXmlParser().Parse(xml);
        var mapped = TicketSqlMapper.Map(parsed.Ticket!);

        Assert.Equal(0m, mapped.Details[0].ImporteUnitario);
        Assert.Equal(0L, mapped.NEncargo);
        Assert.Equal(0, mapped.IdMesa);
        Assert.Equal("0", mapped.IdClient);
        Assert.DoesNotContain(mapped.Warnings, w => w.Field == "ImporteUnitario");
    }

    [Fact]
    public void Texto_demasiado_largo_no_se_trunca()
    {
        var overflow = TicketBaiSkeleton.WrapFactura(detalles: $"""
            <IDDetalleFactura>
            <DescripcionDetalle>{new string('X', 251)}</DescripcionDetalle>
            <Cantidad>1.000</Cantidad>
            <ImporteUnitario>0.00000000</ImporteUnitario>
            <ImporteTotal>3.00</ImporteTotal>
            </IDDetalleFactura>
            """);
        var mapped = TicketSqlMapper.Map(new TicketXmlParser().Parse(overflow).Ticket!);
        Assert.Null(mapped.Details[0].Descripcion);
        Assert.Contains(mapped.Warnings, w => w.Code == "LONGITUD_EXCEDIDA" && w.RawValue!.Length == 251);
    }

    [Fact]
    public void DateOnly_y_TimeOnly_del_ticket_se_convierten_sin_perder_valores()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var mapped = TicketSqlMapper.Map(new TicketXmlParser().Parse(xml).Ticket!);

        Assert.Equal(new DateOnly(2026, 8, 15), mapped.FechaExpedicion);
        Assert.Equal(new TimeOnly(10, 7, 59), mapped.HoraExpedicion);
        Assert.Null(mapped.FechaOperacion);

        var fecha = SqlTemporal.ToDbDate(mapped.FechaExpedicion);
        var hora = SqlTemporal.ToDbTime(mapped.HoraExpedicion);

        Assert.NotNull(fecha);
        Assert.NotNull(hora);
        Assert.Equal(new DateOnly(2026, 8, 15), DateOnly.FromDateTime(fecha.Value));
        Assert.Equal(TimeSpan.Zero, fecha.Value.TimeOfDay);
        Assert.Equal(new TimeOnly(10, 7, 59), TimeOnly.FromTimeSpan(hora.Value));
        Assert.Null(SqlTemporal.ToDbDate(mapped.FechaOperacion));
    }
}

public sealed class TicketQualityEvaluatorTests
{
    [Fact]
    public void Incompatibilidad_xsd_conocida_no_cambia_calidad()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var ticket = new TicketXmlParser().Parse(xml).Ticket!;
        Assert.Equal(TicketQualityStatuses.Ok, TicketQualityEvaluator.Evaluate(ticket, []));
    }

    [Fact]
    public void Warning_de_dato_produce_con_advertencias()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var ticket = new TicketXmlParser().Parse(xml).Ticket!;
        var warnings = new[]
        {
            new ConversionWarning
            {
                Code = "CLAVE_IVA_NO_PERSISTIBLE",
                Field = "ClaveRegimenIvaOpTrascendencia",
                Message = "no persistible",
                RawValue = "ABC"
            }
        };
        Assert.Equal(TicketQualityStatuses.ConAdvertencias, TicketQualityEvaluator.Evaluate(ticket, warnings));
    }

    [Fact]
    public void Filename_no_afecta_calidad_del_ticket()
    {
        Assert.False(WarningText.AffectsTicketQuality(new ConversionWarning
        {
            Code = "DISCREPANCIA_FILENAME_XML",
            Field = "NumFactura",
            Message = "discrepancia",
            RawValue = "99"
        }));
    }

    [Fact]
    public void Identidad_incompleta_es_incompleto()
    {
        var xml = TicketBaiSkeleton.WrapFactura();
        var ticket = new TicketXmlParser().Parse(xml).Ticket!;
        var incomplete = new ParsedTicket
        {
            NifEmisor = null,
            NumFactura = ticket.NumFactura,
            FechaExpedicion = ticket.FechaExpedicion
        };
        Assert.Equal(TicketQualityStatuses.Incompleto, TicketQualityEvaluator.Evaluate(incomplete, []));
    }
}
