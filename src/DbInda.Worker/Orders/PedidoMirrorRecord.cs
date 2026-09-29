namespace DbInda.Worker.Orders;

public sealed class PedidoMirrorSource
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
}

public sealed class PedidoMirrorRecord
{
    public string Sha256 { get; set; } = "";
    public string State { get; set; } = "";
    public string FinalName { get; set; } = "";
    public string StagingName { get; set; } = "";
    public List<PedidoMirrorSource> Sources { get; set; } = [];
    public DateTimeOffset? PreparedUtc { get; set; }
    public DateTimeOffset? MirroredUtc { get; set; }
}
