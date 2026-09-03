using DbInda.Worker.Parsing;

namespace DbInda.Tests.Parsing;

public sealed class SerieFacturaExtractorTests
{
    [Fact]
    public void Compuesta_tienda_tpv_serie()
    {
        var parts = SerieFacturaExtractor.Parse("52.2.1");
        Assert.Equal(52, parts.Tienda);
        Assert.Equal(2, parts.Tpv);
        Assert.Equal("1", parts.SerieDocumento);
    }

    [Fact]
    public void Compuesta_conserva_el_resto_tras_tienda_y_tpv()
    {
        var parts = SerieFacturaExtractor.Parse("52.2.1.9");
        Assert.Equal(52, parts.Tienda);
        Assert.Equal(2, parts.Tpv);
        Assert.Equal("1.9", parts.SerieDocumento);
    }

    [Fact]
    public void Serie_simple_no_inventa_tienda_ni_tpv()
    {
        var parts = SerieFacturaExtractor.Parse("1");
        Assert.Null(parts.Tienda);
        Assert.Null(parts.Tpv);
        Assert.Equal("1", parts.SerieDocumento);
    }

    [Fact]
    public void No_reconocible_se_conserva_integra()
    {
        var parts = SerieFacturaExtractor.Parse("AB.CD");
        Assert.Null(parts.Tienda);
        Assert.Null(parts.Tpv);
        Assert.Equal("AB.CD", parts.SerieDocumento);
    }

    [Fact]
    public void Dos_segmentos_numericos_no_son_formato_compuesto()
    {
        var parts = SerieFacturaExtractor.Parse("52.2");
        Assert.Null(parts.Tienda);
        Assert.Null(parts.Tpv);
        Assert.Equal("52.2", parts.SerieDocumento);
    }
}
