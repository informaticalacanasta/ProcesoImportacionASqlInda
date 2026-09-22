using Dapper;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Files;

public interface IArchivedInvoiceLookup
{
    Task<IReadOnlyList<string>> FindAsync(string originXmlPath, CancellationToken cancellationToken);
}

// Read-only: PDFs follow the actual archived XML, including collision-safe names.
public sealed class ArchivedInvoiceLookup(SqlConnectionFactory connections) : IArchivedInvoiceLookup
{
    public async Task<IReadOnlyList<string>> FindAsync(string originXmlPath, CancellationToken cancellationToken)
    {
        using var connection = connections.Create();
        var rows = await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT RUTA_FINAL FROM dbo.TICKET_RECEPCION
            WHERE RUTA_ORIGEN = @Origin AND ESTADO_ARCHIVO = 'ARCHIVADO'
              AND ESTADO IN ('PROCESADO', 'PROCESADO_CON_ADVERTENCIAS', 'DUPLICADO', 'CONFLICTO_MISMA_FACTURA')
              AND RUTA_FINAL IS NOT NULL
            """, new { Origin = originXmlPath }, cancellationToken: cancellationToken));
        return rows.Distinct(StringComparer.Ordinal).ToArray();
    }
}