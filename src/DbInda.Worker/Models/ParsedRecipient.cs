namespace DbInda.Worker.Models;

public sealed class ParsedRecipient
{
    public int NumOrden { get; init; }
    public string? Nif { get; init; }
    public string? CodigoPais { get; init; }
    public string? IdType { get; init; }
    public string? IdOtro { get; init; }
    public string? ApellidosNombre { get; init; }
    public string? CodigoPostal { get; init; }
    public string? Direccion { get; init; }
}
