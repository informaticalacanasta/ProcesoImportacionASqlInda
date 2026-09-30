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

// Each published directory is also the durable publication record. The downloader
// removes only its XML, never the directory/manifest, preventing republication.
public sealed class TicketDeliveryPublisher
{
    public async Task PublishAsync(string root, TicketDeliveryItem item, CancellationToken token)
    {
        if (item.Id <= 0 || item.Hash.Length != 64 || !item.Hash.All(Uri.IsHexDigit))
            throw new IOException("Identidad o hash de ticket inválido.");
        var final = Path.Combine(root, item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (System.IO.Directory.Exists(final))
        {
            var saved = JsonSerializer.Deserialize<TicketDeliveryItem>(await File.ReadAllTextAsync(Path.Combine(final, "manifest.json"), token));
            if (saved != item) throw new IOException("Registro de entrega de ticket incompatible.");
            return;
        }
        var name = Path.GetFileName(item.Source);
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new IOException("La entrega de tickets solo publica XML.");
        var staging = Path.Combine(root, ".staging", item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        System.IO.Directory.CreateDirectory(staging);
        var target = Path.Combine(staging, name);
        await using (var source = File.OpenRead(item.Source))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(output, token);
            output.Flush(true);
        }
        if (!Sha256FileHasher.ComputeHex(target).Equals(item.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("La copia del ticket no coincide con SQL. No se publica.");
        await using (var manifest = new FileStream(Path.Combine(staging, "manifest.json"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(manifest, item, cancellationToken: token);
            manifest.Flush(true);
        }
        System.IO.Directory.Move(staging, final);
    }
}
