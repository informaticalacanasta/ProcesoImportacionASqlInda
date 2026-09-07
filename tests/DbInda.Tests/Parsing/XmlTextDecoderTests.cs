using System.Text;
using DbInda.Worker.Files;
using DbInda.Worker.Models;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Validation;

namespace DbInda.Tests.Parsing;

public sealed class XmlTextDecoderTests
{
    private readonly TicketDocumentReader _reader = new();
    private readonly TicketXsdValidator _xsd = new(FixtureFile.XsdDirectory, "tiquets.xsd");

    [Fact]
    public void Utf8_correcto_recupera_ordinal_sin_warning()
    {
        var xml = EncodingTestXml.WithDVendedor("ANA Mª");
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.True(EncodingTestXml.Contains(bytes, EncodingTestXml.Utf8FeminineOrdinal));

        var decoded = XmlTextDecoder.Decode(bytes);
        var parse = Read(decoded, EncodingTestXml.MatchingFileName("1"));
        var xsd = _xsd.Validate(decoded.Text);

        Assert.Null(decoded.Warning);
        Assert.True(parse.Success);
        Assert.Equal("ANA Mª", parse.Ticket!.DVendedor);
        Assert.DoesNotContain(parse.Warnings, w => w.Code == "ENCODING_FALLBACK_WINDOWS1252");
        Assert.NotEqual(XsdValidationStatuses.NoValidable, xsd.EstadoValidacionXsd);
    }

    [Fact]
    public void Declaracion_utf8_con_bytes_windows1252_usa_fallback_y_warning()
    {
        var xml = EncodingTestXml.WithDVendedor("ANA Mª");
        var utf8 = Encoding.UTF8.GetBytes(xml);
        var bytes = EncodingTestXml.ReplaceFeminineOrdinalWithWindows1252(utf8);
        Assert.Contains((byte)0xAA, bytes);
        Assert.True(EncodingTestXml.DoesNotContain(bytes, EncodingTestXml.Utf8FeminineOrdinal));

        var decoded = XmlTextDecoder.Decode(bytes);
        var parse = Read(decoded, EncodingTestXml.MatchingFileName("1"));
        var xsd = _xsd.Validate(decoded.Text);

        Assert.True(parse.Success);
        Assert.Equal("ANA Mª", parse.Ticket!.DVendedor);
        var warning = Assert.Single(parse.Warnings, w => w.Code == "ENCODING_FALLBACK_WINDOWS1252");
        Assert.Contains("Windows-1252", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UTF-8", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(decoded.Warning, warning);
        Assert.NotEqual(XsdValidationStatuses.NoValidable, xsd.EstadoValidacionXsd);
        Assert.False(WarningText.AffectsTicketQuality(warning));
        Assert.Contains("ENCODING_FALLBACK_WINDOWS1252", WarningText.Join(parse.Warnings));
    }

    [Fact]
    public void Ascii_normal_sin_warning()
    {
        var xml = EncodingTestXml.WithDVendedor("1137RAQUEL");
        var bytes = Encoding.UTF8.GetBytes(xml);

        var decoded = XmlTextDecoder.Decode(bytes);
        var parse = Read(decoded, EncodingTestXml.MatchingFileName("1"));

        Assert.Null(decoded.Warning);
        Assert.True(parse.Success);
        Assert.Equal("1137RAQUEL", parse.Ticket!.DVendedor);
        Assert.DoesNotContain(parse.Warnings, w => w.Code == "ENCODING_FALLBACK_WINDOWS1252");
    }

    [Fact]
    public void Sha256_se_calcula_sobre_bytes_originales_no_sobre_texto_recodificado()
    {
        var xml = EncodingTestXml.WithDVendedor("ANA Mª");
        var original = EncodingTestXml.ReplaceFeminineOrdinalWithWindows1252(Encoding.UTF8.GetBytes(xml));
        var decoded = XmlTextDecoder.Decode(original);
        var recodedUtf8 = Encoding.UTF8.GetBytes(decoded.Text);

        Assert.Contains("ANA Mª", decoded.Text, StringComparison.Ordinal);
        Assert.NotEqual(original, recodedUtf8);
        Assert.NotEqual(
            Sha256FileHasher.ComputeHex(original),
            Sha256FileHasher.ComputeHex(recodedUtf8));
    }

    [Fact]
    public void Fallback_es_generico_para_otros_caracteres_windows1252()
    {
        var xml = EncodingTestXml.WithDVendedor("PRECIO 10€");
        var utf8 = Encoding.UTF8.GetBytes(xml);
        var bytes = EncodingTestXml.ReplaceUtf8EuroWithWindows1252(utf8);

        var decoded = XmlTextDecoder.Decode(bytes);
        var parse = Read(decoded, EncodingTestXml.MatchingFileName("1"));

        Assert.Equal("ENCODING_FALLBACK_WINDOWS1252", decoded.Warning!.Code);
        Assert.Equal("PRECIO 10€", parse.Ticket!.DVendedor);
    }

    private ParseResult Read(XmlTextDecodeResult decoded, string fileName)
        => _reader.Read(decoded.Text, fileName, decoded.Warning);
}

internal static class EncodingTestXml
{
    public static readonly byte[] Utf8FeminineOrdinal = [0xC2, 0xAA];

    public static string WithDVendedor(string dVendedor, string numFactura = "1")
        => TicketBaiSkeleton.WrapFactura(
            numFactura: numFactura,
            extraCabecera: $"<DVendedor>{dVendedor}</DVendedor>");

    public static string MatchingFileName(string numFactura)
        => $"Fact_B29189644_1_52-2-{numFactura}_20260815_100759_3.00_sin_firmar.xml";

    public static byte[] ReplaceFeminineOrdinalWithWindows1252(byte[] utf8)
        => ReplaceUtf8Sequence(utf8, Utf8FeminineOrdinal, [0xAA]);

    public static byte[] ReplaceUtf8EuroWithWindows1252(byte[] utf8)
        => ReplaceUtf8Sequence(utf8, [0xE2, 0x82, 0xAC], [0x80]);

    public static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }

        return false;
    }

    public static bool DoesNotContain(byte[] haystack, byte[] needle)
        => !Contains(haystack, needle);

    private static byte[] ReplaceUtf8Sequence(byte[] utf8, byte[] from, byte[] to)
    {
        var result = new List<byte>(utf8.Length);
        var replaced = false;
        for (var i = 0; i < utf8.Length; i++)
        {
            if (i + from.Length <= utf8.Length && SequenceEqual(utf8, i, from))
            {
                result.AddRange(to);
                i += from.Length - 1;
                replaced = true;
                continue;
            }

            result.Add(utf8[i]);
        }

        if (!replaced)
            throw new InvalidOperationException("No se encontró la secuencia UTF-8 a sustituir.");

        return result.ToArray();
    }

    private static bool SequenceEqual(byte[] source, int index, byte[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (source[index + i] != expected[i])
                return false;
        }

        return true;
    }
}
