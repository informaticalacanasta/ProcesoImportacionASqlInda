namespace DbInda.Worker.Models;

public sealed class ConversionWarning
{
    public required string Code { get; init; }
    public required string Field { get; init; }
    public required string Message { get; init; }
    public string? RawValue { get; init; }
}
