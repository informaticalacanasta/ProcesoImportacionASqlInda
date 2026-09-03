using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using DbInda.Worker.Configuration;

namespace DbInda.Worker.Persistence;

public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IOptions<SqlOptions> options)
        : this(options.Value.DbInda)
    {
    }

    public SqlConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public SqlConnection Create() => new(_connectionString);
}
