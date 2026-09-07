using System.Text;
using DbInda.Worker.Models;

namespace DbInda.Worker.Parsing;

public sealed class XmlTextDecodeResult
{
    public required string Text { get; init; }
    public ConversionWarning? Warning { get; init; }
}

/// <summary>
/// Interpreta XML de ticket a partir de los bytes físicos.
/// Primero UTF-8 estricto; si hay bytes inválidos, Windows-1252 completo.
/// No modifica los bytes originales.
/// </summary>
public static class XmlTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Windows1252 = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    public static XmlTextDecodeResult Decode(byte[] bytes)
        => Decode(bytes.AsSpan());

    public static XmlTextDecodeResult Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new XmlTextDecodeResult
            {
                Text = StrictUtf8.GetString(bytes),
                Warning = null
            };
        }
        catch (DecoderFallbackException)
        {
            return new XmlTextDecodeResult
            {
                Text = Windows1252.GetString(bytes),
                Warning = new ConversionWarning
                {
                    Code = "ENCODING_FALLBACK_WINDOWS1252",
                    Field = "Encoding",
                    Message = "El XML declara o pretende UTF-8 pero contiene bytes inválidos para UTF-8. Se ha interpretado el documento completo como Windows-1252.",
                    RawValue = "UTF-8"
                }
            };
        }
    }
}
