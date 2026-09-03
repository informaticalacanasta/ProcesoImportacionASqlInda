using DbInda.Worker.Models;
using DbInda.Worker.Validation;

namespace DbInda.Tests.Validation;

public sealed class TicketXsdValidatorTests
{
    private static TicketXsdValidator CreateValidator()
        => new(FixtureFile.XsdDirectory, "tiquets.xsd");

    [Fact]
    public void Xml_real_sin_firma_no_es_validacion_literal_ok()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var result = CreateValidator().Validate(xml);

        Assert.False(result.XsdValido);
        Assert.NotEmpty(result.Events);
        Assert.Equal(
            XsdValidationStatuses.InvalidoIncompatibilidadConocida,
            result.EstadoValidacionXsd);
        Assert.Contains(result.Events, e => e.IsKnownIncompatibility && e.Message.Contains("XMLDSig"));
    }

    [Fact]
    public void Xml_mal_formado_es_no_validable()
    {
        var result = CreateValidator().Validate("<TicketBai>");
        Assert.Null(result.XsdValido);
        Assert.Equal(XsdValidationStatuses.NoValidable, result.EstadoValidacionXsd);
    }

    [Fact]
    public void Recoge_varios_eventos()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-430_20260815_234606_10.50_sin_firmar.xml");
        var result = CreateValidator().Validate(xml);
        Assert.True(result.Events.Count >= 1);
    }

    [Fact]
    public void Error_xsd_de_datos_se_clasifica_aparte()
    {
        var xml = TicketBaiSkeleton.WrapFactura().Replace("<IDVersionTBAI>1.2</IDVersionTBAI>", "<IDVersionTBAI>9.9</IDVersionTBAI>");
        var result = CreateValidator().Validate(xml);
        Assert.False(result.XsdValido);
        Assert.Equal(XsdValidationStatuses.InvalidoDatos, result.EstadoValidacionXsd);
    }

    [Fact]
    public void Signature_presente_no_se_considera_validada()
    {
        var xml = TicketBaiSkeleton.WrapFactura().Replace(
            "</T:TicketBai>",
            """<ds:Signature xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:SignedInfo /></ds:Signature></T:TicketBai>""");
        var result = CreateValidator().Validate(xml);

        Assert.False(result.XsdValido);
        Assert.Contains(
            result.Events,
            e => e.IsKnownIncompatibility && e.Message == KnownXsdIncompatibility.XmlDsigSignatureNotValidatedMessage);
        Assert.DoesNotContain(result.Events, e => e.Message.Contains("firmado correctamente", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(XsdValidationStatuses.InvalidoIncompatibilidadConocida, result.EstadoValidacionXsd);
    }

    [Fact]
    public void No_existe_stub_xmldsig()
    {
        Assert.False(File.Exists(Path.Combine(FixtureFile.XsdDirectory, "xmldsig-core-schema.xsd")));
    }
}
