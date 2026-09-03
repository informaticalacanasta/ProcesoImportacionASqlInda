namespace DbInda.Worker.Models;

public sealed class XsdValidationEvent
{
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public bool IsKnownIncompatibility { get; init; }
}

public sealed class XsdValidationResult
{
    public bool? XsdValido { get; init; }
    public required string EstadoValidacionXsd { get; init; }
    public IReadOnlyList<XsdValidationEvent> Events { get; init; } = [];
}
