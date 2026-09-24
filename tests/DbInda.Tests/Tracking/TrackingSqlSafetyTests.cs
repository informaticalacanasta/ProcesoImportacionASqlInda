using Microsoft.Data.SqlClient;

namespace DbInda.Tests.Tracking;

public sealed class TrackingSqlFactAttribute : FactAttribute
{
    public TrackingSqlFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DBINDA_TEST_TRACKING"), "1", StringComparison.Ordinal))
        {
            Skip = "Pendiente de un SQL Server de pruebas autorizado. Definir DBINDA_TEST_TRACKING=1 y DBINDA_TEST_TRACKING_CONNECTION. No usa la cadena por defecto ni producción.";
        }
    }
}

public sealed class TrackingSqlSafetyTests
{
    [TrackingSqlFact]
    public void Un_error_capturable_del_seguimiento_no_impide_confirmar_el_trabajo_previo()
    {
        using var connection = OpenAuthorized();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "CREATE TABLE #ticket_ok (Id int NOT NULL); INSERT INTO #ticket_ok (Id) VALUES (1);");
        Execute(connection, transaction, """
            SAVE TRANSACTION TrackingMark;
            BEGIN TRY
                INSERT INTO dbo.IMPORTADOR_INTENTO_NO_EXISTE_PRUEBA (ID_INTENTO) VALUES (NEWID());
                IF XACT_STATE() <> 1
                    THROW 50000, 'La transaccion de negocio ya no es confirmable.', 1;
                SELECT 1;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() = 1
                    ROLLBACK TRANSACTION TrackingMark;
                SELECT 0;
            END CATCH
            """);
        Execute(connection, transaction, "INSERT INTO #ticket_ok (Id) VALUES (2);");
        transaction.Commit();

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM #ticket_ok;";
        var count = (int)read.ExecuteScalar()!;
        Assert.Equal(2, count);
    }

    [TrackingSqlFact]
    public void Con_XACT_ABORT_un_error_de_seguimiento_deja_la_transaccion_no_confirmable()
    {
        using var connection = OpenAuthorized();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "SET XACT_ABORT ON; CREATE TABLE #ticket_abort (Id int NOT NULL); INSERT INTO #ticket_abort (Id) VALUES (1);");
        var doomed = Record.Exception(() => Execute(connection, transaction, "INSERT INTO dbo.IMPORTADOR_INTENTO_NO_EXISTE_PRUEBA (ID_INTENTO) VALUES (NEWID());"));
        Assert.NotNull(doomed);
        var commit = Record.Exception(() => transaction.Commit());
        Assert.NotNull(commit);
    }

    private static SqlConnection OpenAuthorized()
    {
        var connectionString = Environment.GetEnvironmentVariable("DBINDA_TEST_TRACKING_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DBINDA_TEST_TRACKING=1 exige DBINDA_TEST_TRACKING_CONNECTION con una base de pruebas, no producción.");

        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
