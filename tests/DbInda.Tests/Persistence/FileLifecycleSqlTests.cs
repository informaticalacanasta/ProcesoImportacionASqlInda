using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Inbound;
using DbInda.Worker.Models;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Processing;
using DbInda.Worker.Validation;
using DbInda.Tests.Inbound;

namespace DbInda.Tests.Persistence;

[Collection("DbIndaSql")]
public sealed class FileLifecycleSqlTests
{
    [SqlFact]
    public async Task Error_sql_reutiliza_la_misma_recepcion_e_incrementa_intento()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("A");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-A_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);

        try
        {
            var firstId = await SeedReceptionAsync(path, hash, ReceptionStatuses.ErrorSql, intento: 1, primerIntento: DateTime.Now.AddMinutes(-9));
            cleanup.TrackReception(firstId);

            await ctx.Processor.ProcessAsync(path, CancellationToken.None);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var rows = (await connection.QueryAsync<(long Id, int Intentos, string Estado)>(
                "SELECT ID_RECEPCION, NUMERO_INTENTO, ESTADO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash })).ToList();
            Assert.Single(rows);
            Assert.Equal(firstId, rows[0].Id);
            Assert.Equal(2, rows[0].Intentos);
            Assert.True(rows[0].Estado is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);
            var primer = await connection.ExecuteScalarAsync<DateTime>(
                "SELECT FECHA_PRIMER_INTENTO FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = firstId });
            Assert.True(DateTime.Now - primer > TimeSpan.FromMinutes(8));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Pendiente_tras_crash_se_reutiliza()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("B");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-B_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            var id = await SeedReceptionAsync(path, hash, ReceptionStatuses.Pendiente, intento: 1, primerIntento: DateTime.Now);
            cleanup.TrackReception(id);
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(long Id, int Intentos, string Estado)>(
                "SELECT ID_RECEPCION, NUMERO_INTENTO, ESTADO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(id, row.Id);
            Assert.Equal(2, row.Intentos);
            Assert.True(row.Estado is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [Fact]
    public async Task Sql_caido_antes_de_recepcion_deja_el_xml_en_entrada()
    {
        using var ctx = LifecycleContext.Create(connectionString: "Server=127.0.0.1,1;Database=DbInda;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1");
        var path = ctx.WriteEntrada("caido.xml", UniqueXml("C"));
        await ctx.Processor.ProcessAsync(path, CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(ctx.Errores, "*.xml", SearchOption.AllDirectories));
    }

    [SqlFact]
    public async Task Procesado_archiva_bytes_identicos_y_ruta_final()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("D");
        var fileName = "Fact_B29189644_1_52-2-D_20260815_100759_3.00_sin_firmar.xml";
        var path = ctx.WriteEntrada(fileName, xml);
        var original = File.ReadAllBytes(path);
        var hash = Sha256FileHasher.ComputeHex(original);
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            var archived = Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories);
            Assert.Single(archived);
            Assert.Equal(original, File.ReadAllBytes(archived[0]));
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var rutaFinal = await connection.ExecuteScalarAsync<string>(
                "SELECT RUTA_FINAL FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(Path.GetFullPath(archived[0]), Path.GetFullPath(rutaFinal!));
            Assert.Contains(Path.Combine("2026", "08", "15"), rutaFinal);
            var estadoArchivo = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO_ARCHIVO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(ArchiveStatuses.Archivado, estadoArchivo);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Procesado_con_advertencias_tambien_va_a_procesados()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("E");
        var path = ctx.WriteEntrada("nombre-no-reconocido-E.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories));
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var estado = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(ReceptionStatuses.ProcesadoConAdvertencias, estado);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Nueva_llegada_duplicada_genera_recepcion_y_archiva()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("F");
        var pathA = ctx.WriteEntrada("Fact_B29189644_1_52-2-F_20260815_100759_3.00_sin_firmar.xml", xml);
        var pathB = ctx.WriteEntrada("Fact_B29189644_1_52-2-F_20260815_100759_3.00_b_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(pathA));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            await ctx.Processor.ProcessAsync(pathA, CancellationToken.None);
            await ctx.Processor.ProcessAsync(pathB, CancellationToken.None);
            Assert.False(File.Exists(pathA));
            Assert.False(File.Exists(pathB));
            Assert.Equal(2, Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories).Length);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var estados = (await connection.QueryAsync<string>(
                "SELECT ESTADO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash ORDER BY ID_RECEPCION",
                new { Hash = hash })).ToList();
            Assert.Equal(2, estados.Count);
            Assert.Contains(estados, e => e is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);
            Assert.Contains(ReceptionStatuses.Duplicado, estados);
            var tickets = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(1, tickets);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Conflicto_no_crea_segundo_ticket_y_archiva()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("G");
        var xmlB = xml.Replace("<ImporteTotalFactura>3.00</ImporteTotalFactura>", "<ImporteTotalFactura>4.00</ImporteTotalFactura>");
        var pathA = ctx.WriteEntrada("Fact_B29189644_1_52-2-G_20260815_100759_3.00_sin_firmar.xml", xml);
        var pathB = ctx.WriteEntrada("Fact_B29189644_1_52-2-G_20260815_100759_4.00_sin_firmar.xml", xmlB);
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(Sha256FileHasher.ComputeHex(File.ReadAllBytes(pathA)));
        cleanup.TrackHash(Sha256FileHasher.ComputeHex(File.ReadAllBytes(pathB)));
        try
        {
            await ctx.Processor.ProcessAsync(pathA, CancellationToken.None);
            await ctx.Processor.ProcessAsync(pathB, CancellationToken.None);
            Assert.False(File.Exists(pathB));
            Assert.Equal(2, Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories).Length);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var tickets = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE NUM_FACTURA = @Num",
                new { Num = ExtractNum(xml) });
            Assert.Equal(1, tickets);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Xml_malformed_va_a_errores()
    {
        using var ctx = LifecycleContext.Create();
        var path = ctx.WriteEntrada("roto.xml", "<no-cerrado");
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(ctx.Errores, "*.xml", SearchOption.AllDirectories));
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var estado = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(ReceptionStatuses.ErrorXml, estado);
            var tickets = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(0, tickets);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Colision_de_nombre_conserva_ambos_xml()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("I");
        var fileName = "Fact_B29189644_1_52-2-I_20260815_100759_3.00_sin_firmar.xml";
        foreach (var tiendaFolder in new[] { "52", XmlFileArchiver.SinTiendaFolder })
        {
            var occupantDir = Directory.CreateDirectory(Path.Combine(ctx.Procesados, "2026", "08", "15", tiendaFolder)).FullName;
            File.WriteAllText(Path.Combine(occupantDir, fileName), "<ocupante />");
        }

        var path = ctx.WriteEntrada(fileName, xml);
        var original = File.ReadAllBytes(path);
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(Sha256FileHasher.ComputeHex(original));
        try
        {
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            var occupants = Directory.GetFiles(ctx.Procesados, fileName, SearchOption.AllDirectories);
            Assert.NotEmpty(occupants);
            foreach (var occupant in occupants)
                Assert.Equal("<ocupante />", File.ReadAllText(occupant));
            var archived = Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories)
                .Where(p => !occupants.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Assert.Single(archived);
            Assert.Equal(original, File.ReadAllBytes(archived[0]));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Fallo_de_move_no_duplica_importacion_y_luego_archiva()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("J");
        var fileName = "Fact_B29189644_1_52-2-J_20260815_100759_3.00_sin_firmar.xml";
        var path = ctx.WriteEntrada(fileName, xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        var failing = new SequenceArchiver(ctx.Archiver) { FailuresLeft = 1 };
        var processor = ctx.CreateProcessor(failing);
        try
        {
            await processor.ProcessAsync(path, CancellationToken.None);
            Assert.True(File.Exists(path));
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var first = await connection.QuerySingleAsync<(long Id, string Estado, string? RutaFinal)>(
                "SELECT ID_RECEPCION, ESTADO, RUTA_FINAL FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.True(first.Estado is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);
            Assert.Null(first.RutaFinal);
            var archivo = await connection.QuerySingleAsync<(string EstadoArchivo, string? Prevista)>(
                "SELECT ESTADO_ARCHIVO, RUTA_DESTINO_PREVISTA FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = first.Id });
            Assert.Equal(ArchiveStatuses.Archivando, archivo.EstadoArchivo);
            Assert.False(string.IsNullOrWhiteSpace(archivo.Prevista));

            await processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            var second = await connection.QuerySingleAsync<(long Id, int Count, string? RutaFinal)>(
                """
                SELECT MIN(ID_RECEPCION), COUNT(*), MAX(RUTA_FINAL)
                FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash
                """,
                new { Hash = hash });
            Assert.Equal(1, second.Count);
            Assert.Equal(first.Id, second.Id);
            Assert.False(string.IsNullOrWhiteSpace(second.RutaFinal));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Restart_reintenta_error_sql_porque_el_xml_sigue_en_entrada()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("K");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-K_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            var id = await SeedReceptionAsync(path, hash, ReceptionStatuses.ErrorSql, 1, DateTime.Now);
            cleanup.TrackReception(id);
            var restarted = LifecycleContext.Create(root: ctx.Root);
            await restarted.Processor.ProcessAsync(path, CancellationToken.None);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(long Id, int Intentos)>(
                "SELECT ID_RECEPCION, NUMERO_INTENTO FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(id, row.Id);
            Assert.Equal(2, row.Intentos);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Error_sql_no_archiva_el_xml()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("L");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-L_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            var id = await SeedReceptionAsync(path, hash, ReceptionStatuses.ErrorSql, 1, DateTime.Now);
            cleanup.TrackReception(id);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var estadoArchivo = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO_ARCHIVO FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = id });
            Assert.Equal(ArchiveStatuses.Pendiente, estadoArchivo);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(ctx.Errores, "*.xml", SearchOption.AllDirectories));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Misma_ruta_mismo_hash_segunda_llegada_conserva_copia_propia()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("M");
        var fileName = "Fact_B29189644_1_52-2-M_20260815_100759_3.00_sin_firmar.xml";
        var path = ctx.WriteEntrada(fileName, xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        try
        {
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            File.WriteAllText(path, xml);
            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            var archived = Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories);
            Assert.Equal(2, archived.Length);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var rows = (await connection.QueryAsync<(string Estado, string EstadoArchivo, string RutaFinal)>(
                "SELECT ESTADO, ESTADO_ARCHIVO, RUTA_FINAL FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash ORDER BY ID_RECEPCION",
                new { Hash = hash })).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(ArchiveStatuses.Archivado, r.EstadoArchivo));
            Assert.NotEqual(rows[0].RutaFinal, rows[1].RutaFinal);
            Assert.Contains(rows, r => r.Estado is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);
            Assert.Contains(rows, r => r.Estado == ReceptionStatuses.Duplicado);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Reconciliacion_A_mueve_origen_a_prevista()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("A1");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-A1_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        var failing = new SequenceArchiver(ctx.Archiver) { FailuresLeft = 1 };
        var processor = ctx.CreateProcessor(failing);
        try
        {
            await processor.ProcessAsync(path, CancellationToken.None);
            Assert.True(File.Exists(path));
            var state = await ReadArchiveStateAsync(hash);
            Assert.Equal(ArchiveStatuses.Archivando, state.EstadoArchivo);
            Assert.False(File.Exists(state.Prevista!));

            await ctx.CreateReconciler(ctx.Archiver).ReconcileAsync(CancellationToken.None);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(state.Prevista!));
            var after = await ReadArchiveStateAsync(hash);
            Assert.Equal(ArchiveStatuses.Archivado, after.EstadoArchivo);
            Assert.Equal(Path.GetFullPath(state.Prevista!), Path.GetFullPath(after.RutaFinal!));
            Assert.Single(Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Reconciliacion_B_destino_existe_origen_no()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("B1");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-B1_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        var failing = new SequenceArchiver(ctx.Archiver) { FailuresLeft = 1 };
        var processor = ctx.CreateProcessor(failing);
        try
        {
            await processor.ProcessAsync(path, CancellationToken.None);
            var state = await ReadArchiveStateAsync(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(state.Prevista!)!);
            File.Move(path, state.Prevista!);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(state.Prevista!));

            await ctx.CreateReconciler(ctx.Archiver).ReconcileAsync(CancellationToken.None);

            Assert.False(File.Exists(path));
            Assert.True(File.Exists(state.Prevista!));
            var after = await ReadArchiveStateAsync(hash);
            Assert.Equal(ArchiveStatuses.Archivado, after.EstadoArchivo);
            Assert.Equal(Path.GetFullPath(state.Prevista!), Path.GetFullPath(after.RutaFinal!));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Reconciliacion_C_no_borra_origen_nueva_llegada()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("C1");
        var fileName = "Fact_B29189644_1_52-2-C1_20260815_100759_3.00_sin_firmar.xml";
        var path = ctx.WriteEntrada(fileName, xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        var failing = new SequenceArchiver(ctx.Archiver) { FailuresLeft = 1 };
        var processor = ctx.CreateProcessor(failing);
        try
        {
            await processor.ProcessAsync(path, CancellationToken.None);
            var state = await ReadArchiveStateAsync(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(state.Prevista!)!);
            File.Copy(path, state.Prevista!);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(state.Prevista!));

            await ctx.CreateReconciler(ctx.Archiver).ReconcileAsync(CancellationToken.None);

            Assert.True(File.Exists(path), "El origen no se borra: puede ser una nueva llegada física.");
            Assert.True(File.Exists(state.Prevista!));
            var after = await ReadArchiveStateAsync(hash);
            Assert.Equal(ArchiveStatuses.Archivado, after.EstadoArchivo);
            Assert.Equal(Path.GetFullPath(state.Prevista!), Path.GetFullPath(after.RutaFinal!));

            await ctx.Processor.ProcessAsync(path, CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Equal(2, Directory.GetFiles(ctx.Procesados, "*.xml", SearchOption.AllDirectories).Length);
            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET_RECEPCION WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(2, count);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Reconciliacion_D_sin_origen_ni_destino_error_archivo()
    {
        using var ctx = LifecycleContext.Create();
        var xml = UniqueXml("D1");
        var path = ctx.WriteEntrada("Fact_B29189644_1_52-2-D1_20260815_100759_3.00_sin_firmar.xml", xml);
        var hash = Sha256FileHasher.ComputeHex(File.ReadAllBytes(path));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(hash);
        var failing = new SequenceArchiver(ctx.Archiver) { FailuresLeft = 1 };
        var processor = ctx.CreateProcessor(failing);
        try
        {
            await processor.ProcessAsync(path, CancellationToken.None);
            var state = await ReadArchiveStateAsync(hash);
            File.Delete(path);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(state.Prevista!));

            await ctx.CreateReconciler(ctx.Archiver).ReconcileAsync(CancellationToken.None);

            var after = await ReadArchiveStateAsync(hash);
            Assert.Equal(ArchiveStatuses.ErrorArchivo, after.EstadoArchivo);
            Assert.Null(after.RutaFinal);
            Assert.False(File.Exists(path));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    private static string UniqueXml(string prefix)
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = prefix + Guid.NewGuid().ToString("N")[..10];
        return xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
    }

    private static string ExtractNum(string xml)
    {
        var start = xml.IndexOf("<NumFactura>", StringComparison.Ordinal) + "<NumFactura>".Length;
        var end = xml.IndexOf("</NumFactura>", StringComparison.Ordinal);
        return xml[start..end];
    }

    private static async Task<long> SeedReceptionAsync(
        string path, string hash, string estado, int intento, DateTime primerIntento)
    {
        var receptions = new ReceptionRepository();
        await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
        await connection.OpenAsync();
        return await receptions.InsertAsync(
            connection,
            null,
            new ReceptionInsert
            {
                FechaRecepcion = primerIntento,
                NombreFichero = Path.GetFileName(path),
                RutaOrigen = FilePathNormalizer.Normalize(path),
                HashSha256 = hash,
                TamanoBytes = new FileInfo(path).Length,
                Estado = estado,
                NumeroIntento = intento,
                FechaPrimerIntento = primerIntento,
                FechaUltimoIntento = primerIntento,
                XsdValido = false,
                EstadoValidacionXsd = XsdValidationStatuses.InvalidoIncompatibilidadConocida
            },
            CancellationToken.None);
    }

    private static async Task<(string EstadoArchivo, string? Prevista, string? RutaFinal)> ReadArchiveStateAsync(string hash)
    {
        await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
        await connection.OpenAsync();
        return await connection.QuerySingleAsync<(string EstadoArchivo, string? Prevista, string? RutaFinal)>(
            """
            SELECT ESTADO_ARCHIVO, RUTA_DESTINO_PREVISTA, RUTA_FINAL
            FROM dbo.TICKET_RECEPCION
            WHERE HASH_SHA256 = @Hash
            """,
            new { Hash = hash });
    }

    private sealed class SequenceArchiver : IXmlFileArchiver
    {
        private readonly IXmlFileArchiver _inner;
        public int FailuresLeft { get; set; }

        public SequenceArchiver(IXmlFileArchiver inner) => _inner = inner;

        public string AllocateDestination(ArchiveRequest request) => _inner.AllocateDestination(request);

        public Task MoveToExactAsync(string sourcePath, string destinationPath, string expectedHash, CancellationToken cancellationToken)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new IOException("disco o permisos simulados");
            }

            return _inner.MoveToExactAsync(sourcePath, destinationPath, expectedHash, cancellationToken);
        }

        public bool Exists(string path) => _inner.Exists(path);

        public string? TryComputeHash(string path) => _inner.TryComputeHash(path);
    }

    private sealed class LifecycleContext : IDisposable
    {
        private readonly bool _ownsRoot;

        private LifecycleContext(
            string root,
            string entrada,
            string procesados,
            string errores,
            TicketInboundFileProcessor processor,
            XmlFileArchiver archiver,
            bool ownsRoot)
        {
            Root = root;
            Entrada = entrada;
            Procesados = procesados;
            Errores = errores;
            Processor = processor;
            Archiver = archiver;
            _ownsRoot = ownsRoot;
        }

        public string Root { get; }
        public string Entrada { get; }
        public string Procesados { get; }
        public string Errores { get; }
        public TicketInboundFileProcessor Processor { get; }
        public XmlFileArchiver Archiver { get; }

        public static LifecycleContext Create(string? connectionString = null, string? root = null)
        {
            var owns = root is null;
            root ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dbinda-3b-" + Guid.NewGuid().ToString("N"))).FullName;
            var entrada = Directory.CreateDirectory(Path.Combine(root, "entrada")).FullName;
            var procesados = Directory.CreateDirectory(Path.Combine(root, "procesados")).FullName;
            var errores = Directory.CreateDirectory(Path.Combine(root, "errores")).FullName;
            var paths = Options.Create(new PathsOptions
            {
                Input = entrada,
                Processed = procesados,
                Errors = errores,
                Xsd = FixtureFile.XsdDirectory,
                Logs = root
            });
            var archiver = new XmlFileArchiver(paths, NullLogger<XmlFileArchiver>.Instance);
            var processor = BuildProcessor(connectionString ?? SqlTestEnvironment.ConnectionString, archiver);
            return new LifecycleContext(root, entrada, procesados, errores, processor, archiver, owns);
        }

        public TicketInboundFileProcessor CreateProcessor(IXmlFileArchiver archiver)
            => BuildProcessor(SqlTestEnvironment.ConnectionString, archiver);

        public XmlArchiveReconciler CreateReconciler(IXmlFileArchiver archiver)
            => BuildReconciler(SqlTestEnvironment.ConnectionString, archiver);

        public string WriteEntrada(string name, string contents)
        {
            var path = Path.Combine(Entrada, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (!_ownsRoot)
                return;
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
            }
        }

        private static TicketInboundFileProcessor BuildProcessor(string connectionString, IXmlFileArchiver archiver)
        {
            var factory = new SqlConnectionFactory(connectionString);
            var receptions = new ReceptionRepository();
            var importer = new TicketImportProcessor(factory, receptions, new TicketRepository());
            var reconciler = new XmlArchiveReconciler(
                factory, receptions, archiver, NullLogger<XmlArchiveReconciler>.Instance);
            return new TicketInboundFileProcessor(
                new TicketDocumentReader(),
                new TicketXsdValidator(FixtureFile.XsdDirectory, "tiquets.xsd"),
                importer,
                archiver,
                reconciler,
                new SqlRetryScheduler(Options.Create(new RetryOptions()), TimeProvider.System),
                NullLogger<TicketInboundFileProcessor>.Instance);
        }

        private static XmlArchiveReconciler BuildReconciler(string connectionString, IXmlFileArchiver archiver)
        {
            var factory = new SqlConnectionFactory(connectionString);
            return new XmlArchiveReconciler(
                factory, new ReceptionRepository(), archiver, NullLogger<XmlArchiveReconciler>.Instance);
        }
    }
}
