using System.Text;
using DbInda.Worker.Parsing;

namespace DbInda.Tests.Parsing;

public sealed class TicketFileNameParserTests
{
    private readonly TicketFileNameParser _parser = new();

    [Fact]
    public void Formato_A_sin_nif_se_reconoce()
    {
        var parsed = _parser.Parse("Fact_1_60-2-19919_20260601_152522_2.20.xml");

        Assert.True(parsed.PatternMatched);
        Assert.Null(parsed.NifEmisor);
        Assert.Equal("1", parsed.TokenIntermedio);
        Assert.Equal(60, parsed.Tienda);
        Assert.Equal(2, parsed.Tpv);
        Assert.Equal("19919", parsed.NumFactura);
        Assert.Equal(new DateOnly(2026, 6, 1), parsed.Fecha);
        Assert.Equal(new TimeOnly(15, 25, 22), parsed.Hora);
        Assert.Equal(2.20m, parsed.Importe);
        Assert.DoesNotContain(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Formato_B_con_nif_y_sin_firmar_se_reconoce()
    {
        var parsed = _parser.Parse("fact_B29189644_1_66-1-42553_20260727_210734_1.80_sin_firmar.xml");

        Assert.True(parsed.PatternMatched);
        Assert.Equal("B29189644", parsed.NifEmisor);
        Assert.Equal("1", parsed.TokenIntermedio);
        Assert.Equal(66, parsed.Tienda);
        Assert.Equal(1, parsed.Tpv);
        Assert.Equal("42553", parsed.NumFactura);
        Assert.Equal(new DateOnly(2026, 7, 27), parsed.Fecha);
        Assert.Equal(new TimeOnly(21, 7, 34), parsed.Hora);
        Assert.Equal(1.80m, parsed.Importe);
        Assert.DoesNotContain(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Formato_B_con_num_factura_corto_se_reconoce()
    {
        var parsed = _parser.Parse("Fact_B29189644_1_52-2-6_20260815_141315_12.00_sin_firmar.xml");

        Assert.True(parsed.PatternMatched);
        Assert.Equal("B29189644", parsed.NifEmisor);
        Assert.Equal(52, parsed.Tienda);
        Assert.Equal(2, parsed.Tpv);
        Assert.Equal("6", parsed.NumFactura);
        Assert.Equal(new DateOnly(2026, 8, 15), parsed.Fecha);
        Assert.Equal(new TimeOnly(14, 13, 15), parsed.Hora);
        Assert.Equal(12.00m, parsed.Importe);
        Assert.DoesNotContain(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Formato_B_sin_sufijo_sin_firmar_se_reconoce()
    {
        var parsed = _parser.Parse("Fact_B29189644_1_52-2-6_20260815_141315_12.00.xml");

        Assert.True(parsed.PatternMatched);
        Assert.Equal("B29189644", parsed.NifEmisor);
        Assert.Equal(52, parsed.Tienda);
        Assert.Equal(2, parsed.Tpv);
        Assert.Equal("6", parsed.NumFactura);
        Assert.Equal(new DateOnly(2026, 8, 15), parsed.Fecha);
        Assert.Equal(12.00m, parsed.Importe);
        Assert.DoesNotContain(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Nombre_invalido_sigue_generando_filename_no_reconocido()
    {
        var parsed = _parser.Parse("nombre raro.xml");

        Assert.False(parsed.PatternMatched);
        Assert.Null(parsed.NifEmisor);
        Assert.Contains(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Theory]
    [InlineData("Fact_1_60-2-19919_20260601_152522_2.20.xml")]
    [InlineData("fact_1_60-2-19919_20260601_152522_2.20.xml")]
    [InlineData("FACT_B29189644_1_66-1-42553_20260727_210734_1.80_sin_firmar.xml")]
    [InlineData("fact_B29189644_1_66-1-42553_20260727_210734_1.80_sin_firmar.xml")]
    public void Prefijo_Fact_es_case_insensitive(string fileName)
    {
        var parsed = _parser.Parse(fileName);

        Assert.True(parsed.PatternMatched);
        Assert.DoesNotContain(parsed.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
    }

    [Fact]
    public void Formato_A_no_inventa_nif()
    {
        var parsed = _parser.Parse("Fact_1_60-2-19919_20260601_152522_2.20.xml");
        Assert.Null(parsed.NifEmisor);
    }

    [Fact]
    public void Formato_A_reconocido_no_elimina_warning_de_encoding()
    {
        var xml = EncodingTestXml.WithDVendedor("ANA Mª");
        var bytes = EncodingTestXml.ReplaceFeminineOrdinalWithWindows1252(Encoding.UTF8.GetBytes(xml));
        var decoded = XmlTextDecoder.Decode(bytes);
        var parse = new TicketDocumentReader().Read(
            decoded.Text,
            "Fact_1_60-2-19919_20260601_152522_2.20.xml",
            decoded.Warning);

        Assert.True(parse.FileName!.PatternMatched);
        Assert.DoesNotContain(parse.Warnings, w => w.Code == "FILENAME_NO_RECONOCIDO");
        Assert.Contains(parse.Warnings, w => w.Code == "ENCODING_FALLBACK_WINDOWS1252");
    }
}
