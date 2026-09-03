using DbInda.Worker.Persistence;

namespace DbInda.Tests.Persistence;

public sealed class SqlUniqueConstraintTests
{
    [Fact]
    public void Reconoce_violacion_de_UX_TICKET_HASH_SHA256()
    {
        Assert.True(SqlUniqueConstraint.IsTicketHashDuplicate(
            2627,
            "Violation of UNIQUE KEY constraint 'UX_TICKET_HASH_SHA256'. Cannot insert duplicate key in object 'dbo.TICKET'."));
        Assert.True(SqlUniqueConstraint.IsTicketHashDuplicate(
            2601,
            "Cannot insert duplicate key row in object 'dbo.TICKET' with unique index 'UX_TICKET_HASH_SHA256'."));
    }

    [Fact]
    public void No_trata_otras_unique_como_duplicado_de_hash()
    {
        Assert.False(SqlUniqueConstraint.IsTicketHashDuplicate(
            2627,
            "Violation of UNIQUE KEY constraint 'UX_TICKET_DETALLE_LINEA'."));
        Assert.False(SqlUniqueConstraint.IsTicketHashDuplicate(
            547,
            "The INSERT statement conflicted with the FOREIGN KEY constraint 'FK_TICKET_DETALLE_TICKET'."));
        Assert.False(SqlUniqueConstraint.IsTicketHashDuplicate(2627, null));
        Assert.False(SqlUniqueConstraint.IsTicketHashDuplicate(1205, "UX_TICKET_HASH_SHA256"));
    }
}
