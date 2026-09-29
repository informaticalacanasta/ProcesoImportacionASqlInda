using System.Globalization;
using System.Text;
using DbInda.Worker.Orders;
using DbInda.Worker.Parsing;

namespace DbInda.Tests.Orders;

public sealed class PedidoTxtParserTests
{
    private readonly PedidoTxtParser _parser = new();

    [Fact]
    public void La_cabecera_nombra_66_columnas_y_el_fichero_trae_67_campos()
    {
        Assert.Equal(66, PedidoColumns.Header.Length);
        Assert.Equal(PedidoColumns.NamedCount, PedidoColumns.Header.Length);
        Assert.Equal(67, PedidoColumns.Count);
        Assert.Equal(66, PedidoSamples.HeaderLine().Split('|').Length - 1);
        Assert.Equal(67, PedidoSamples.HeaderLine().Split('|').Length);
        Assert.Equal("", PedidoSamples.HeaderLine().Split('|')[^1]);
        Assert.Equal("id_internet", PedidoSamples.HeaderLine().Split('|')[65]);
    }

    [Fact]
    public void Una_linea_conserva_articulo_fecha_del_nombre_y_numero_de_linea()
    {
        var result = _parser.Parse(PedidoSamples.Name, PedidoSamples.File());
        Assert.True(result.Success, result.Error);
        var document = result.Document!;
        Assert.Equal(new DateTime(2026, 9, 23, 12, 51, 18), document.Header.OrderedAt);
        Assert.Equal(167, document.Header.StoreId);
        Assert.Equal(1, document.Header.CashRegisterId);
        Assert.Equal(5118, document.Header.OrderNumber);
        Assert.Equal(1, document.Header.LineCount);
        var line = Assert.Single(document.Lines);
        Assert.Equal(2, line.LineNumber);
        Assert.Equal("000023200", line.ArticleId);
    }

    [Fact]
    public void Varias_lineas_cuentan_todas_y_conservan_la_linea_fisica()
    {
        var text = PedidoSamples.File(PedidoSamples.Line(), PedidoSamples.Line(), PedidoSamples.Line());
        var result = _parser.Parse(PedidoSamples.Name, text);
        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Document!.Header.LineCount);
        Assert.Equal([2, 3, 4], result.Document.Lines.Select(line => line.LineNumber).ToArray());
    }

    [Fact]
    public void El_prefijo_del_organizador_no_es_la_tienda()
    {
        var result = _parser.Parse(PedidoSamples.Prefixed, PedidoSamples.File());
        Assert.True(result.Success, result.Error);
        Assert.Equal(167, result.Document!.Identity.StoreId);
        Assert.Equal(1, result.Document.Identity.CashRegisterId);
        Assert.Equal(PedidoSamples.Name, result.Document.Identity.LogicalName);
    }

    [Fact]
    public void Cantidad_y_medida_negativas_y_cero_se_conservan()
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells =>
        {
            PedidoSamples.Set(cells, "QUANTITAT", "-2,5");
            PedidoSamples.Set(cells, "MESURA", "-1.25");
            PedidoSamples.Set(cells, "PREU", "0");
        }));
        var line = Assert.Single(_parser.Parse(PedidoSamples.Name, text).Document!.Lines);
        Assert.Equal(-2.5m, line.Quantity);
        Assert.Equal(-1.25m, line.Measure);
        Assert.Equal(0m, line.Price);
    }

    [Fact]
    public void Los_vacios_quedan_en_null()
    {
        var line = Assert.Single(_parser.Parse(PedidoSamples.Name, PedidoSamples.File()).Document!.Lines);
        Assert.Null(line.Description);
        Assert.Null(line.Comments);
        Assert.Null(line.EsEncargo);
        Assert.Null(line.ServeDate);
    }

    [Theory]
    [InlineData("69,12", 69.12)]
    [InlineData("69.12", 69.12)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    public void El_decimal_no_depende_de_la_cultura(string raw, decimal expected)
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells => PedidoSamples.Set(cells, "PREU", raw)));
        var line = Assert.Single(_parser.Parse(PedidoSamples.Name, text).Document!.Lines);
        Assert.Equal(expected, line.Price);
    }

    [Theory]
    [InlineData("False", false)]
    [InlineData("True", true)]
    [InlineData("Verdadero", true)]
    [InlineData("falso", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Los_booleanos_aceptan_las_formas_del_tpv(string raw, bool expected)
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells => PedidoSamples.Set(cells, "ESENCARGO", raw)));
        var document = _parser.Parse(PedidoSamples.Name, text).Document!;
        Assert.Equal(expected, document.Lines[0].EsEncargo);
        Assert.Equal(expected, document.Header.EsEncargo);
    }

    [Fact]
    public void Fecha_y_hora_usan_el_formato_fijo()
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells =>
        {
            PedidoSamples.Set(cells, "DATAASERVIR", "24/09/2026");
            PedidoSamples.Set(cells, "HORAASERVIR", "12:51:17");
            PedidoSamples.Set(cells, "HORA_INICI", "08:05:09");
        }));
        var line = Assert.Single(_parser.Parse(PedidoSamples.Name, text).Document!.Lines);
        Assert.Equal(new DateTime(2026, 9, 24), line.ServeDate);
        Assert.Equal(new TimeSpan(12, 51, 17), line.ServeTime);
        Assert.Equal(new TimeSpan(8, 5, 9), line.StartTime);
        Assert.Equal(new DateTime(2026, 9, 23, 12, 51, 18), _parser.Parse(PedidoSamples.Name, text).Document!.Header.OrderedAt);
    }

    [Fact]
    public void Falta_una_columna_de_cabecera()
    {
        var header = string.Join("|", PedidoColumns.Header.Take(PedidoColumns.NamedCount - 1)) + "|";
        var result = _parser.Parse(PedidoSamples.Name, header + "\n" + PedidoSamples.Line());
        Assert.False(result.Success);
        Assert.Contains("66", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Una_linea_con_menos_de_66_columnas_es_invalida()
    {
        var result = _parser.Parse(PedidoSamples.Name, PedidoSamples.HeaderLine() + "\n" + string.Join("|", PedidoSamples.Cells().Take(65)));
        Assert.False(result.Success);
        Assert.Contains("65", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Una_linea_con_mas_de_66_columnas_es_invalida()
    {
        var result = _parser.Parse(PedidoSamples.Name, PedidoSamples.HeaderLine() + "\n" + PedidoSamples.Line() + "|extra");
        Assert.False(result.Success);
        Assert.Contains("68", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiendas_distintas_dentro_del_fichero_son_un_error()
    {
        var text = PedidoSamples.File(
            PedidoSamples.Line(),
            PedidoSamples.Line(cells => PedidoSamples.Set(cells, "ID_tienda", "168")));
        var result = _parser.Parse(PedidoSamples.Name, text);
        Assert.False(result.Success);
        Assert.Contains("ID_tienda", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cajas_distintas_dentro_del_fichero_son_un_error()
    {
        var text = PedidoSamples.File(
            PedidoSamples.Line(),
            PedidoSamples.Line(cells => PedidoSamples.Set(cells, "ID_CAIXA", "2")));
        Assert.False(_parser.Parse(PedidoSamples.Name, text).Success);
    }

    [Fact]
    public void Ordrelinia_distinta_dentro_del_fichero_es_un_error()
    {
        var text = PedidoSamples.File(
            PedidoSamples.Line(),
            PedidoSamples.Line(cells => PedidoSamples.Set(cells, "ORDRELINIA", "999")));
        Assert.False(_parser.Parse(PedidoSamples.Name, text).Success);
    }

    [Fact]
    public void La_tienda_del_nombre_debe_coincidir_con_la_del_fichero()
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells => PedidoSamples.Set(cells, "ID_tienda", "168")));
        var result = _parser.Parse(PedidoSamples.Name, text);
        Assert.False(result.Success);
        Assert.Contains("nombre", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT", true, 167, 1)]
    [InlineData("50167_PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT", true, 167, 1)]
    [InlineData("90167_PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT", true, 167, 1)]
    [InlineData("CLIENTES.TXT", false, 0, 0)]
    [InlineData("80052_CLIENTES.TXT", false, 0, 0)]
    [InlineData("PED_0260923125118_00167_00001.TXT", false, 0, 0)]
    public void El_nombre_reconoce_el_segmento_de_pedido(string name, bool isOrder, int store, int cash)
    {
        Assert.Equal(isOrder, PedidoFileClassifier.IsOrderFile(name));
        if (!isOrder)
            return;
        Assert.True(PedidoFileClassifier.TryMatch(name, out var identity));
        Assert.Equal(store, identity.StoreId);
        Assert.Equal(cash, identity.CashRegisterId);
        Assert.Equal(new DateTime(2026, 9, 23, 12, 51, 18), identity.OrderedAt);
    }

    [Fact]
    public void El_importador_ignora_parciales_ocultos_y_staging()
    {
        Assert.False(PedidoFileClassifier.IsImportCandidate(".PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT"));
        Assert.False(PedidoFileClassifier.IsImportCandidate("PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT.partial"));
        Assert.False(PedidoFileClassifier.IsImportCandidate("AAAA.partial"));
        Assert.False(PedidoFileClassifier.IsImportCandidate(new string('A', 64) + "__" + PedidoSamples.Name));
        Assert.True(PedidoFileClassifier.IsImportCandidate(PedidoSamples.Name));
        var suffixed = PedidoFileClassifier.WithHashSuffix(PedidoSamples.Name, new string('A', 64));
        Assert.True(PedidoFileClassifier.IsImportCandidate(suffixed));
    }

    [Fact]
    public void Un_nombre_con_repetido_del_organizador_sigue_siendo_pedido()
    {
        var name = "50167_PED_0260923125118_00167_00001_PEDIDOS_TMPP_REPETIDO_20260924_105000_abc.TXT";
        Assert.True(PedidoFileClassifier.TryMatch(name, out var identity));
        Assert.Equal(PedidoSamples.Name, identity.LogicalName);
        Assert.Equal(167, identity.StoreId);
    }

    [Fact]
    public void Windows_1252_conserva_la_descripcion()
    {
        var text = PedidoSamples.File(PedidoSamples.Line(cells => PedidoSamples.Set(cells, "DESCRIPCIO", "niño")));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes(text);
        var decoded = XmlTextDecoder.Decode(bytes).Text;
        var line = Assert.Single(_parser.Parse(PedidoSamples.Name, decoded).Document!.Lines);
        Assert.Equal("niño", line.Description);
    }

    [Theory]
    [InlineData("20167_PED_0260923124219_00167_00001_PEDIDOS_TMPP.TXT", 167, 1, 2026, 9, 23, 12, 42, 19, 2, "1443526550219")]
    [InlineData("PED_0251102124251_00070_00002_PEDIDOS_TMPP.TXT", 70, 2, 2025, 11, 2, 12, 42, 51, 1, "309039400")]
    [InlineData("PED_0251030224834_00165_00001_PEDIDOS_TMPP.TXT", 165, 1, 2025, 10, 30, 22, 48, 34, 13, "1730305310319")]
    public void Un_txt_real_tiene_67_campos_y_el_ultimo_no_desplaza_el_resto(
        string fileName, int store, int cash, int year, int month, int day, int hour, int minute, int second,
        int lines, string articleId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Orders", fileName);
        var text = XmlTextDecoder.Decode(File.ReadAllBytes(path)).Text;
        var physical = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        Assert.All(physical, line =>
        {
            var cells = line.Split('|');
            Assert.Equal(67, cells.Length);
            Assert.Equal("", cells[^1]);
        });
        Assert.Equal("id_internet", physical[0].Split('|')[65]);

        var result = _parser.Parse(fileName, text);
        Assert.True(result.Success, result.Error);
        var document = result.Document!;
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), document.Header.OrderedAt);
        Assert.Equal(store, document.Header.StoreId);
        Assert.Equal(cash, document.Header.CashRegisterId);
        Assert.Equal(lines, document.Lines.Count);
        Assert.Equal(articleId, document.Lines[0].ArticleId);
        Assert.Null(document.Lines[0].InternetId);
        Assert.Contains(document.Lines, line => line.Quantity < 0);
    }

    [Fact]
    public void Un_sello_imposible_no_inventa_la_fecha()
    {
        var name = "PED_0261323125118_00167_00001_PEDIDOS_TMPP.TXT";
        Assert.True(PedidoFileClassifier.TryMatch(name, out var identity));
        Assert.Null(identity.OrderedAt);
        Assert.False(_parser.Parse(name, PedidoSamples.File()).Success);
    }
}

internal static class PedidoSamples
{
    public const string Name = "PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT";
    public const string Prefixed = "50167_PED_0260923125118_00167_00001_PEDIDOS_TMPP.TXT";

    public static string HeaderLine() => string.Join("|", PedidoColumns.Header) + "|";

    public static string[] Cells()
    {
        var cells = new string[PedidoColumns.Header.Length];
        Set(cells, "ID_tienda", "167");
        Set(cells, "ID_CAIXA", "1");
        Set(cells, "ORDRELINIA", "5118");
        Set(cells, "ID_ARTICLE", "000023200");
        Set(cells, "QUANTITAT", "1");
        return cells;
    }

    public static void Set(string[] cells, string name, string value)
        => cells[PedidoColumns.Index(name)] = value;

    public static string Line(Action<string[]>? mutate = null)
    {
        var cells = Cells();
        mutate?.Invoke(cells);
        return string.Join("|", cells) + "|";
    }

    public static string File(params string[] dataLines)
    {
        var lines = dataLines.Length == 0 ? [Line()] : dataLines;
        return HeaderLine() + "\n" + string.Join("\n", lines) + "\n";
    }
}
