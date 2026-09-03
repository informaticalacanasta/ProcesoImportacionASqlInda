namespace DbInda.Worker.Persistence;

public sealed class ExistingTicketRef
{
    public long IdTicket { get; init; }
    public long IdRecepcionOrigen { get; init; }
    public string HashSha256 { get; init; } = "";
}
