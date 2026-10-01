using System.Text.Json;
using Dapper;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Files;

public sealed class TicketDeliveryOptions
{
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "/home/tpv_recepcion/Tickets/Salida";
    public long MinimumReceptionId { get; set; }
    public int ScanIntervalSeconds { get; set; } = 30;
}

public sealed record TicketDeliveryItem(long Id, string Source, string Hash);

public sealed class TicketDeliveryLookup(SqlConnectionFactory connections)
{
    public async Task<IEnumerable<TicketDeliveryItem>> ReadAsync(long minimum, long after, CancellationToken token)
    {
        using var connection = connections.Create();
        return await connection.QueryAsync<TicketDeliveryItem>(new CommandDefinition("""
            SELECT TOP (500) ID_RECEPCION AS Id, RUTA_FINAL AS Source, HASH_SHA256 AS Hash
            FROM dbo.TICKET_RECEPCION
            WHERE ID_RECEPCION >= @minimum AND ID_RECEPCION > @after
              AND ESTADO_ARCHIVO = 'ARCHIVADO'
              AND ESTADO IN ('PROCESADO', 'PROCESADO_CON_ADVERTENCIAS')
              AND RUTA_FINAL IS NOT NULL AND HASH_SHA256 IS NOT NULL
            ORDER BY ID_RECEPCION
            """, new { minimum, after }, cancellationToken: token));
    }
}

// Each published directory is also the durable publication record.
public sealed class TicketDeliveryPublisher
{
    public async Task PublishAsync(string root, TicketDeliveryItem item, CancellationToken token)
    {
        if (item.Id <= 0 || item.Hash.Length != 64 || !item.Hash.All(Uri.IsHexDigit))
            throw new IOException("Identidad o hash de ticket inválido.");
        var name = Path.GetFileName(item.Source);
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new IOException("La entrega de tickets requiere un XML.");
        var (normalPdf, a4Pdf) = PdfSources(item.Source);
        var final = Path.Combine(root, item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (System.IO.Directory.Exists(final))
        {
            var saved = JsonSerializer.Deserialize<TicketDeliveryItem>(await File.ReadAllTextAsync(Path.Combine(final, "manifest.json"), token));
            if (saved != item) throw new IOException("Registro de entrega de ticket incompatible.");
            // Completa entregas XML creadas por versiones anteriores cuando llegan los PDF.
            if (!File.Exists(Path.Combine(final, Path.GetFileName(normalPdf))) && !File.Exists(normalPdf))
                throw new FileNotFoundException("PDF normal pendiente.", normalPdf);
            if (!File.Exists(Path.Combine(final, Path.GetFileName(a4Pdf))) && !File.Exists(a4Pdf))
                throw new FileNotFoundException("PDF A4 pendiente.", a4Pdf);
            await CompletePdfAsync(normalPdf, final, token);
            await CompletePdfAsync(a4Pdf, final, token);
            return;
        }
        if (!File.Exists(normalPdf)) throw new FileNotFoundException("PDF normal pendiente.", normalPdf);
        if (!File.Exists(a4Pdf)) throw new FileNotFoundException("PDF A4 pendiente.", a4Pdf);
        var staging = Path.Combine(root, ".staging", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        System.IO.Directory.CreateDirectory(staging);
        await CopyVerifiedAsync(item.Source, Path.Combine(staging, name), item.Hash, token);
        await CopyVerifiedAsync(normalPdf, Path.Combine(staging, Path.GetFileName(normalPdf)), Sha256FileHasher.ComputeHex(normalPdf), token);
        await CopyVerifiedAsync(a4Pdf, Path.Combine(staging, Path.GetFileName(a4Pdf)), Sha256FileHasher.ComputeHex(a4Pdf), token);
        await using (var manifest = new FileStream(Path.Combine(staging, "manifest.json"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(manifest, item, cancellationToken: token);
            manifest.Flush(true);
        }
        System.IO.Directory.Move(staging, final);
    }

    private static (string Normal, string A4) PdfSources(string xml)
    {
        var stem = Path.GetFileNameWithoutExtension(xml);
        var a4Stem = stem.Replace("_sin_firmar", "_a4_sin_firmar", StringComparison.OrdinalIgnoreCase);
        if (a4Stem == stem)
            throw new IOException($"No se puede identificar el PDF A4 del XML '{xml}'.");
        var directory = Path.GetDirectoryName(xml)!;
        return (Path.Combine(directory, stem + ".pdf"), Path.Combine(directory, a4Stem + ".pdf"));
    }

    private static async Task CompletePdfAsync(string source, string final, CancellationToken token)
    {
        var target = Path.Combine(final, Path.GetFileName(source));
        if (File.Exists(target)) return;
        if (!File.Exists(source)) throw new FileNotFoundException("PDF pendiente.", source);
        var partial = target + ".partial";
        await CopyVerifiedAsync(source, partial, Sha256FileHasher.ComputeHex(source), token);
        File.Move(partial, target, overwrite: false);
    }

    private static async Task CopyVerifiedAsync(string source, string target, string expectedHash, CancellationToken token)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, token);
            output.Flush(true);
        }
        if (!Sha256FileHasher.ComputeHex(target).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"La copia de '{source}' no conserva su SHA-256.");
    }
}
