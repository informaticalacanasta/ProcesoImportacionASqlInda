using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Persistence;

public static class SqlAvailability
{
    public static bool IsUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException)
                return true;
            if (current is TimeoutException)
                return true;
            if (current is DbException)
                return true;
        }

        return false;
    }
}
