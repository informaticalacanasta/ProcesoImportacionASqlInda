namespace DbInda.Worker.Orders;

public enum PedidoSaveOutcome
{
    Saved,
    Duplicate,
    Transient,
    Permanent
}

public enum PedidoErrorWrite
{
    Recorded,
    AlreadyKnown,
    Transient
}

public sealed class PedidoReceptionRow
{
    public long IdRecepcion { get; init; }
    public string Estado { get; init; } = "";
    public long? IdPedido { get; init; }
    public string? RutaFinal { get; init; }
}

public sealed class PedidoSaveRequest
{
    public required string FileName { get; init; }
    public required string SourcePath { get; init; }
    public required string FinalPath { get; init; }
    public required string Hash { get; init; }
    public required long Size { get; init; }
    public required PedidoHeader Header { get; init; }
    public required IReadOnlyList<PedidoDetail> Lines { get; init; }
    public required int StoreFromName { get; init; }
    public required int CashFromName { get; init; }
}

public sealed class PedidoSaveResult
{
    public PedidoSaveOutcome Outcome { get; init; }
    public long? OrderId { get; init; }
    public long? ReceptionId { get; init; }
    public string? Error { get; init; }

    public static PedidoSaveResult Saved(long orderId, long receptionId) => new()
    {
        Outcome = PedidoSaveOutcome.Saved,
        OrderId = orderId,
        ReceptionId = receptionId
    };

    public static PedidoSaveResult Duplicate() => new() { Outcome = PedidoSaveOutcome.Duplicate };

    public static PedidoSaveResult Transient(string? error) => new()
    {
        Outcome = PedidoSaveOutcome.Transient,
        Error = error
    };

    public static PedidoSaveResult Permanent(string? error) => new()
    {
        Outcome = PedidoSaveOutcome.Permanent,
        Error = error
    };
}

public interface IPedidoRepository
{
    Task<PedidoReceptionRow?> FindByHashAsync(string hash, CancellationToken cancellationToken);

    Task<PedidoSaveResult> SaveAsync(PedidoSaveRequest request, CancellationToken cancellationToken);

    Task<PedidoErrorWrite> TryInsertErrorAsync(
        string fileName,
        string sourcePath,
        string hash,
        long size,
        int? storeFromName,
        int? cashFromName,
        DateTime? fileDate,
        string error,
        CancellationToken cancellationToken);

    Task TryUpdateFinalPathAsync(long receptionId, string finalPath, CancellationToken cancellationToken);
}
