namespace DbInda.Worker.Orders;

public static class PedidoReceptionStatuses
{
    public const string Pendiente = "PENDIENTE";
    public const string Procesando = "PROCESANDO";
    public const string Procesado = "PROCESADO";
    public const string Error = "ERROR";
    public const string Duplicado = "DUPLICADO";
}

public static class PedidoMirrorStates
{
    public const string Prepared = "PREPARED";
    public const string Mirrored = "MIRRORED";
}
