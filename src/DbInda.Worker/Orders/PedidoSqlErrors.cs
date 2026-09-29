using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Orders;

public static class PedidoSqlErrors
{
    private static readonly HashSet<int> TransientNumbers =
    [
        -2, 20, 53, 64, 121, 233, 1205,
        10053, 10054, 10060,
        40197, 40501, 4060, 40613,
        10928, 10929,
        49918, 49919, 49920
    ];

    public static bool IsHashDuplicate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql && IsHashDuplicate(sql))
                return true;
        }

        return false;
    }

    public static bool IsHashDuplicate(int number, string? message)
        => (number == 2627 || number == 2601)
           && message is not null
           && message.Contains("DS_HASH_SHA256", StringComparison.OrdinalIgnoreCase);

    public static bool IsTransient(Exception exception)
    {
        if (IsHashDuplicate(exception))
            return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
            if (current is SqlException sql && IsTransient(sql))
                return true;
        }

        return false;
    }

    public static bool IsTransient(int number, byte severity)
        => severity >= 17 || TransientNumbers.Contains(number);

    private static bool IsHashDuplicate(SqlException exception)
    {
        if (IsHashDuplicate(exception.Number, exception.Message))
            return true;
        foreach (SqlError error in exception.Errors)
        {
            if (IsHashDuplicate(error.Number, error.Message))
                return true;
        }

        return false;
    }

    private static bool IsTransient(SqlException exception)
    {
        if (IsTransient(exception.Number, exception.Class))
            return true;
        foreach (SqlError error in exception.Errors)
        {
            if (IsTransient(error.Number, error.Class))
                return true;
        }

        return false;
    }
}
