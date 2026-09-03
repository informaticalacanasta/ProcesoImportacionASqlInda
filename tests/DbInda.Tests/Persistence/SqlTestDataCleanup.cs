using Dapper;
using Microsoft.Data.SqlClient;
using DbInda.Worker.Processing;

namespace DbInda.Tests.Persistence;

internal sealed class SqlTestDataCleanup
{
    private readonly HashSet<long> _receptionIds = [];
    private readonly HashSet<string> _hashes = [];

    public void Track(ImportResult result)
    {
        if (result.ReceptionId is long id)
            _receptionIds.Add(id);
    }

    public void TrackReception(long idRecepcion) => _receptionIds.Add(idRecepcion);

    public void TrackHash(string hashSha256)
    {
        if (!string.IsNullOrWhiteSpace(hashSha256))
            _hashes.Add(hashSha256);
    }

    public async Task DeleteOwnedAsync()
    {
        if (_receptionIds.Count == 0 && _hashes.Count == 0)
            return;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DeleteOwnedCoreAsync();
                return;
            }
            catch (SqlException ex) when (ex.Number == 1205 && attempt < 3)
            {
                await Task.Delay(50 * attempt);
            }
        }
    }

    private async Task DeleteOwnedCoreAsync()
    {
        await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var hashes = _hashes.ToArray();
        var receptionIds = _receptionIds.ToArray();

        var extraReceptions = hashes.Length == 0
            ? []
            : (await connection.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT ID_RECEPCION FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 IN @Hashes;",
                    new { Hashes = hashes },
                    transaction))).ToArray();

        var allReceptionIds = receptionIds.Concat(extraReceptions).Distinct().ToArray();

        var ticketsByHash = hashes.Length == 0
            ? []
            : (await connection.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT ID_TICKET FROM dbo.TICKET WHERE HASH_SHA256 IN @Hashes;",
                    new { Hashes = hashes },
                    transaction))).ToArray();

        var ticketsByReception = allReceptionIds.Length == 0
            ? []
            : (await connection.QueryAsync<long>(
                new CommandDefinition(
                    """
                    SELECT ID_TICKET FROM dbo.TICKET WHERE ID_RECEPCION_ORIGEN IN @Ids
                    UNION
                    SELECT ID_TICKET FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION IN @Ids AND ID_TICKET IS NOT NULL;
                    """,
                    new { Ids = allReceptionIds },
                    transaction))).ToArray();

        var ticketIds = ticketsByHash.Concat(ticketsByReception).Distinct().ToArray();

        if (allReceptionIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.TICKET_RECEPCION
                SET ID_TICKET = NULL,
                    ID_RECEPCION_ORIGINAL = NULL
                WHERE ID_RECEPCION IN @Ids;
                """,
                new { Ids = allReceptionIds },
                transaction));
        }

        if (ticketIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_DETALLE WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_IVA WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_CLAVE WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_DESTINATARIO WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_RECTIFICACION WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET WHERE ID_TICKET IN @Ids;",
                new { Ids = ticketIds },
                transaction));
        }

        if (allReceptionIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION IN @Ids;",
                new { Ids = allReceptionIds },
                transaction));
        }

        await transaction.CommitAsync();
    }
}
