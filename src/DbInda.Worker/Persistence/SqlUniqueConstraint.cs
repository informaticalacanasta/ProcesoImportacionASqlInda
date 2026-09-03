using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Persistence;

public static class SqlUniqueConstraint
{
    public const string TicketHashName = "UX_TICKET_HASH_SHA256";

    public static bool IsTicketHashDuplicate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && IsTicketHashDuplicate(sql))
                return true;
        }

        return false;
    }

    public static bool IsTicketHashDuplicate(SqlException exception)
    {
        if (IsTicketHashDuplicate(exception.Number, exception.Message))
            return true;

        foreach (SqlError error in exception.Errors)
        {
            if (IsTicketHashDuplicate(error.Number, error.Message))
                return true;
        }

        return false;
    }

    public static bool IsTicketHashDuplicate(int number, string? message)
        => (number == 2627 || number == 2601)
           && message is not null
           && message.Contains(TicketHashName, StringComparison.OrdinalIgnoreCase);
}
