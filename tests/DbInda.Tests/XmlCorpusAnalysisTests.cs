using System.Xml.Linq;
using DbInda.Worker.Parsing;
using Xunit.Abstractions;

namespace DbInda.Tests;

public sealed class XmlCorpusAnalysisTests
{
    private readonly ITestOutputHelper _output;

    public XmlCorpusAnalysisTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Analiza_xml_locales_sin_modificarlos()
    {
        var directory = FindCorpusDirectory();
        if (directory is null)
        {
            _output.WriteLine("No se encontró la carpeta de XML reales. Test omitido.");
            return;
        }

        var reader = new TicketDocumentReader();
        var files = Directory.GetFiles(directory, "*.xml");
        Assert.True(files.Length > 0);

        var processed = 0;
        var failed = 0;
        var details = 0;
        var vat = 0;
        var keys = 0;
        var conversionWarnings = 0;
        var withSignature = 0;
        var withEncadenamiento = 0;
        var withSoftware = 0;
        var withDestinatarios = 0;
        var withRectificativa = 0;
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var file in files)
        {
            var xml = File.ReadAllText(file);
            var result = reader.Read(xml, Path.GetFileName(file));
            if (!result.Success)
            {
                failed++;
                errors.Add($"{Path.GetFileName(file)}: {string.Join("; ", result.Errors)}");
                continue;
            }

            processed++;
            details += result.Ticket!.Details.Count;
            vat += result.Ticket.VatBreakdowns.Count;
            keys += result.Ticket.TaxKeys.Count;
            conversionWarnings += result.Warnings.Count(w => w.Code == "VALOR_NO_CONVERTIBLE");
            foreach (var element in result.UnknownElements)
            {
                var key = element.ParentPath + "/" + element.LocalName;
                unknown[key] = unknown.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            var document = XDocument.Parse(xml);
            if (HasLocalName(document, "Signature"))
                withSignature++;
            if (HasLocalName(document, "EncadenamientoFacturaAnterior"))
                withEncadenamiento++;
            if (HasLocalName(document, "Software"))
                withSoftware++;
            if (HasLocalName(document, "Destinatarios"))
                withDestinatarios++;
            if (HasLocalName(document, "FacturaRectificativa"))
                withRectificativa++;
        }

        _output.WriteLine($"XML procesados: {processed}");
        _output.WriteLine($"XML que no pudieron parsearse: {failed}");
        _output.WriteLine($"Detalles: {details}");
        _output.WriteLine($"Bloques IVA: {vat}");
        _output.WriteLine($"Claves: {keys}");
        _output.WriteLine($"Warnings de conversión: {conversionWarnings}");
        _output.WriteLine(
            "Elementos desconocidos: " +
            (unknown.Count == 0 ? "(ninguno)" : string.Join(", ", unknown.Select(kv => kv.Key + "=" + kv.Value))));
        _output.WriteLine($"Con ds:Signature: {withSignature}");
        _output.WriteLine($"Con EncadenamientoFacturaAnterior (no persistido): {withEncadenamiento}");
        _output.WriteLine($"Con Software (no persistido): {withSoftware}");
        _output.WriteLine($"Con Destinatarios: {withDestinatarios}");
        _output.WriteLine($"Con FacturaRectificativa: {withRectificativa}");
        if (errors.Count > 0)
            _output.WriteLine("Errores: " + string.Join(" | ", errors));

        Assert.Equal(0, failed);
        Assert.Equal(files.Length, processed);
    }

    private static bool HasLocalName(XDocument document, string localName)
        => document.Descendants().Any(e => e.Name.LocalName == localName);

    private static string? FindCorpusDirectory()
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;
        var dir = Path.Combine(root, "xml tiquetbai feria tpv2");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DbInda.sln"))
                && Directory.Exists(Path.Combine(dir.FullName, "xml tiquetbai feria tpv2")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
