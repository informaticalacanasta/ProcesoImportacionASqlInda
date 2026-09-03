using Dapper;
using Microsoft.Data.SqlClient;
using DbInda.Worker.Files;
using DbInda.Worker.Models;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Processing;
using DbInda.Worker.Validation;

namespace DbInda.Tests.Persistence;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (!SqlTestEnvironment.IsReady)
            Skip = SqlTestEnvironment.SkipReason;
    }
}

internal static class SqlTestEnvironment
{
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("DBINDA_TEST")
        ?? "Server=localhost;Database=DbInda;Integrated Security=true;TrustServerCertificate=true";

    public static bool IsReady { get; }
    public static bool ArchiveColumnsReady { get; }
    public static string SkipReason { get; }

    static SqlTestEnvironment()
    {
        try
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            var id = connection.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.TICKET', 'U');");
            if (id is null)
            {
                IsReady = false;
                ArchiveColumnsReady = false;
                SkipReason = "La base DbInda existe pero las tablas no están creadas. Ejecutar sql/01_CreateTables.sql para los tests de persistencia.";
                return;
            }

            var archiveCol = connection.ExecuteScalar<int?>("SELECT COL_LENGTH('dbo.TICKET_RECEPCION', 'ESTADO_ARCHIVO');");
            ArchiveColumnsReady = archiveCol is not null;
            IsReady = ArchiveColumnsReady;
            SkipReason = ArchiveColumnsReady
                ? ""
                : "Ejecutar sql/03_Alter_TICKET_RECEPCION_Archivo.sql en SSMS para los tests de persistencia.";
        }
        catch (Exception ex)
        {
            IsReady = false;
            ArchiveColumnsReady = false;
            SkipReason = "SQL Server no disponible para tests de persistencia: " + ex.Message;
        }
    }

    public static TicketImportProcessor CreateProcessor()
    {
        var factory = new SqlConnectionFactory(ConnectionString);
        return new TicketImportProcessor(factory, new ReceptionRepository(), new TicketRepository());
    }

    public static TicketImportCommand CreateCommand(string xml, string fileName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        var parse = new TicketDocumentReader().Read(xml, fileName);
        var xsd = new TicketXsdValidator(FixtureFile.XsdDirectory, "tiquets.xsd").Validate(xml);
        return new TicketImportCommand
        {
            FileName = fileName,
            OriginPath = @"C:\DbInda\Entrada\" + fileName,
            FileBytes = bytes,
            Parse = parse,
            Xsd = xsd
        };
    }

    public static SqlTestDataCleanup BeginCleanup(params TicketImportCommand[] commands)
    {
        var cleanup = new SqlTestDataCleanup();
        foreach (var command in commands)
            cleanup.TrackHash(Sha256FileHasher.ComputeHex(command.FileBytes));
        return cleanup;
    }
}

[Collection("DbIndaSql")]
public sealed class SqlPersistenceTests
{
    [SqlFact]
    public async Task Importa_ticket_real_y_no_duplica_por_hash()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "T" + Guid.NewGuid().ToString("N")[..12];
        var uniqueXml = xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
        var fileName = $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_sin_firmar.xml";
        var command = SqlTestEnvironment.CreateCommand(uniqueXml, fileName);
        var cleanup = SqlTestEnvironment.BeginCleanup(command);

        try
        {
            var first = await processor.ImportAsync(command);
            cleanup.Track(first);
            Assert.True(
                first.Status is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias,
                first.Status + " " + string.Join("; ", first.Errors));
            Assert.NotNull(first.TicketId);

            var secondSamePath = await processor.ImportAsync(command);
            cleanup.Track(secondSamePath);
            Assert.True(secondSamePath.ArchiveOnly);
            Assert.Equal(first.ReceptionId, secondSamePath.ReceptionId);
            Assert.Equal(first.TicketId, secondSamePath.TicketId);

            var otherArrival = SqlTestEnvironment.CreateCommand(
                uniqueXml, fileName.Replace("_sin_firmar", "_b_sin_firmar"));
            var duplicate = await processor.ImportAsync(otherArrival);
            cleanup.Track(duplicate);
            Assert.Equal(ReceptionStatuses.Duplicado, duplicate.Status);
            Assert.Equal(first.TicketId, duplicate.TicketId);
            Assert.NotEqual(first.ReceptionId, duplicate.ReceptionId);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var ticketCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE ID_TICKET = @Id",
                new { Id = first.TicketId });
            Assert.Equal(1, ticketCount);

            var quality = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO_CALIDAD FROM dbo.TICKET WHERE ID_TICKET = @Id",
                new { Id = first.TicketId });
            Assert.Equal(TicketQualityStatuses.Ok, quality);

            var xsdEstado = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO_VALIDACION_XSD FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = first.ReceptionId });
            Assert.Equal(XsdValidationStatuses.InvalidoIncompatibilidadConocida, xsdEstado);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Conflicto_misma_factura_no_crea_segundo_ticket()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "C" + Guid.NewGuid().ToString("N")[..12];
        var xmlA = xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
        var xmlB = xmlA.Replace("<ImporteTotalFactura>3.00</ImporteTotalFactura>", "<ImporteTotalFactura>4.00</ImporteTotalFactura>");
        var commandA = SqlTestEnvironment.CreateCommand(xmlA, $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_sin_firmar.xml");
        var commandB = SqlTestEnvironment.CreateCommand(xmlB, $"Fact_B29189644_1_52-2-{num}_20260815_100759_4.00_sin_firmar.xml");
        var cleanup = SqlTestEnvironment.BeginCleanup(commandA, commandB);

        try
        {
            var first = await processor.ImportAsync(commandA);
            cleanup.Track(first);
            Assert.NotNull(first.TicketId);

            var second = await processor.ImportAsync(commandB);
            cleanup.Track(second);
            Assert.Equal(ReceptionStatuses.ConflictoMismaFactura, second.Status);
            Assert.Equal(first.TicketId, second.TicketId);
            Assert.NotEqual(first.ReceptionId, second.ReceptionId);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Distinta_tienda_o_tpv_no_es_conflicto_misma_factura()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "S" + Guid.NewGuid().ToString("N")[..12];
        var xmlA = xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
        var xmlB = xmlA.Replace("<SerieFactura>52.2.1</SerieFactura>", "<SerieFactura>66.4.1</SerieFactura>");
        var commandA = SqlTestEnvironment.CreateCommand(xmlA, $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_sin_firmar.xml");
        var commandB = SqlTestEnvironment.CreateCommand(xmlB, $"Fact_B29189644_1_66-4-{num}_20260815_100759_3.00_sin_firmar.xml");
        var cleanup = SqlTestEnvironment.BeginCleanup(commandA, commandB);

        try
        {
            var first = await processor.ImportAsync(commandA);
            cleanup.Track(first);
            Assert.True(
                first.Status is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias,
                first.Status + " " + string.Join("; ", first.Errors));
            Assert.NotNull(first.TicketId);

            var second = await processor.ImportAsync(commandB);
            cleanup.Track(second);
            Assert.True(
                second.Status is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias,
                second.Status + " " + string.Join("; ", second.Errors));
            Assert.NotEqual(first.TicketId, second.TicketId);
            Assert.NotEqual(ReceptionStatuses.ConflictoMismaFactura, second.Status);
            Assert.NotEqual(ReceptionStatuses.Duplicado, second.Status);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var rows = (await connection.QueryAsync<(string Serie, int Tienda, int Tpv)>(
                """
                SELECT SERIE_FACTURA, TIENDA, TPV
                FROM dbo.TICKET
                WHERE NUM_FACTURA = @Num
                ORDER BY TIENDA
                """,
                new { Num = num })).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("1", r.Serie));
            Assert.Equal((52, 2), (rows[0].Tienda, rows[0].Tpv));
            Assert.Equal((66, 4), (rows[1].Tienda, rows[1].Tpv));
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Persiste_encadenamiento_anterior_sin_partir_la_serie()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "E" + Guid.NewGuid().ToString("N")[..12];
        var xmlEncadenado = xml
            .Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>")
            .Replace(
                "<Software>",
                """
                <EncadenamientoFacturaAnterior>
                <SerieFacturaAnterior>66.1.1</SerieFacturaAnterior>
                <NumFacturaAnterior>42382</NumFacturaAnterior>
                <FechaExpedicionFacturaAnterior>27-07-2026</FechaExpedicionFacturaAnterior>
                <SignatureValueFirmaFacturaAnterior>b6ba6f08e8c3aa99d57bb757dde059db98690465e4ef636bc35af925a6d31b8c</SignatureValueFirmaFacturaAnterior>
                </EncadenamientoFacturaAnterior>
                <Software>
                """);
        var command = SqlTestEnvironment.CreateCommand(
            xmlEncadenado, $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_sin_firmar.xml");
        var cleanup = SqlTestEnvironment.BeginCleanup(command);

        try
        {
            var result = await processor.ImportAsync(command);
            cleanup.Track(result);
            Assert.True(
                result.Status is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias,
                result.Status + " " + string.Join("; ", result.Errors));
            Assert.NotNull(result.TicketId);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(string SerieAnt, string NumAnt, DateTime FechaAnt, string HashAnt, string Serie)>(
                """
                SELECT SERIE_FACTURA_ANTERIOR, NUM_FACTURA_ANTERIOR, FECHA_FACTURA_ANTERIOR,
                       HASH_FIRMA_FACTURA_ANTERIOR, SERIE_FACTURA
                FROM dbo.TICKET
                WHERE ID_TICKET = @Id
                """,
                new { Id = result.TicketId });
            Assert.Equal("66.1.1", row.SerieAnt);
            Assert.NotEqual("1", row.SerieAnt);
            Assert.Equal("42382", row.NumAnt);
            Assert.Equal(new DateTime(2026, 7, 27), row.FechaAnt.Date);
            Assert.Equal("b6ba6f08e8c3aa99d57bb757dde059db98690465e4ef636bc35af925a6d31b8c", row.HashAnt);
            Assert.Equal("1", row.Serie);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Error_en_hija_hace_rollback_del_ticket()
    {
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "R" + Guid.NewGuid().ToString("N")[..12];
        var xmlBroken = xml
            .Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>")
            .Replace("<IDDetalleFactura>", "<IDDetalleFactura><IDDetalleFactura>");

        var parse = new TicketXmlParser().Parse(xmlBroken);
        Assert.False(parse.Success);

        var mappedOk = new TicketXmlParser().Parse(
            xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>"));
        var write = TicketSqlMapper.Map(mappedOk.Ticket!);
        var bad = CloneWithInvalidLine(write);
        var receptionHash = Sha256FileHasher.ComputeHex(System.Text.Encoding.UTF8.GetBytes(num));
        var ticketHash = Sha256FileHasher.ComputeHex(System.Text.Encoding.UTF8.GetBytes(num + "x"));
        var cleanup = new SqlTestDataCleanup();
        cleanup.TrackHash(receptionHash);
        cleanup.TrackHash(ticketHash);

        var factory = new SqlConnectionFactory(SqlTestEnvironment.ConnectionString);
        var receptions = new ReceptionRepository();
        var tickets = new TicketRepository();
        await using var connection = factory.Create();
        await connection.OpenAsync();
        try
        {
            var receptionId = await receptions.InsertAsync(
                connection,
                null,
                new ReceptionInsert
                {
                    FechaRecepcion = DateTime.Now,
                    NombreFichero = "rollback.xml",
                    RutaOrigen = @"C:\DbInda\Entrada\rollback.xml",
                    HashSha256 = receptionHash,
                    TamanoBytes = 10,
                    Estado = ReceptionStatuses.Pendiente,
                    FechaPrimerIntento = DateTime.Now,
                    FechaUltimoIntento = DateTime.Now,
                    XsdValido = false,
                    EstadoValidacionXsd = XsdValidationStatuses.InvalidoIncompatibilidadConocida
                },
                CancellationToken.None);
            cleanup.TrackReception(receptionId);

            await using var transaction = await connection.BeginTransactionAsync();
            var failed = false;
            try
            {
                await tickets.InsertGraphAsync(
                    connection,
                    transaction,
                    bad,
                    receptionId,
                    ticketHash,
                    TicketQualityStatuses.Ok,
                    DateTime.Now,
                    0,
                    CancellationToken.None);
                await transaction.CommitAsync();
            }
            catch
            {
                failed = true;
                await transaction.RollbackAsync();
            }

            Assert.True(failed);
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE NUM_FACTURA = @Num",
                new { Num = num });
            Assert.Equal(0, count);

            await receptions.UpdateAsync(
                connection,
                null,
                new ReceptionUpdate
                {
                    IdRecepcion = receptionId,
                    Estado = ReceptionStatuses.ErrorSql,
                    FechaUltimoIntento = DateTime.Now,
                    NumeroErrores = 1,
                    MensajeError = "rollback de prueba"
                },
                CancellationToken.None);

            var estado = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = receptionId });
            Assert.Equal(ReceptionStatuses.ErrorSql, estado);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Clave_invalida_no_inserta_fila_clave()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = TicketBaiSkeleton.WrapFactura(
            numFactura: "K" + Guid.NewGuid().ToString("N")[..12],
            claves: """
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>01</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            <IDClave>
            <ClaveRegimenIvaOpTrascendencia>ZZZ</ClaveRegimenIvaOpTrascendencia>
            </IDClave>
            """);
        var fileName = $"Fact_B29189644_1_52-2-9_{DateTime.Now:yyyyMMdd}_100759_3.00_sin_firmar.xml";
        var command = SqlTestEnvironment.CreateCommand(xml, fileName);
        var cleanup = SqlTestEnvironment.BeginCleanup(command);

        try
        {
            var result = await processor.ImportAsync(command);
            cleanup.Track(result);
            Assert.NotNull(result.TicketId);
            Assert.Contains(result.Warnings, w => w.Code == "CLAVE_IVA_NO_PERSISTIBLE");

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var keys = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET_CLAVE WHERE ID_TICKET = @Id",
                new { Id = result.TicketId });
            Assert.Equal(1, keys);
            var quality = await connection.ExecuteScalarAsync<string>(
                "SELECT ESTADO_CALIDAD FROM dbo.TICKET WHERE ID_TICKET = @Id",
                new { Id = result.TicketId });
            Assert.Equal(TicketQualityStatuses.ConAdvertencias, quality);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Carrera_de_hash_no_termina_en_error_sql()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "H" + Guid.NewGuid().ToString("N")[..12];
        var uniqueXml = xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
        var commandA = SqlTestEnvironment.CreateCommand(
            uniqueXml, $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_a_sin_firmar.xml");
        var commandB = SqlTestEnvironment.CreateCommand(
            uniqueXml, $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_b_sin_firmar.xml");
        var cleanup = SqlTestEnvironment.BeginCleanup(commandA, commandB);

        try
        {
            var first = processor.ImportAsync(commandA);
            var second = processor.ImportAsync(commandB);
            await Task.WhenAll(first, second);

            var results = new[] { first.Result, second.Result };
            foreach (var result in results)
                cleanup.Track(result);

            Assert.DoesNotContain(results, r => r.Status == ReceptionStatuses.ErrorSql);
            Assert.Equal(results[0].TicketId, results[1].TicketId);
            Assert.NotNull(results[0].TicketId);
            Assert.Contains(results, r => r.Status == ReceptionStatuses.Duplicado);
            Assert.Contains(
                results,
                r => r.Status is ReceptionStatuses.Procesado or ReceptionStatuses.ProcesadoConAdvertencias);

            await using var connection = new SqlConnection(SqlTestEnvironment.ConnectionString);
            await connection.OpenAsync();
            var hash = Sha256FileHasher.ComputeHex(commandA.FileBytes);
            var ticketCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(1, ticketCount);

            var duplicate = Assert.Single(results, r => r.Status == ReceptionStatuses.Duplicado);
            var esDuplicado = await connection.ExecuteScalarAsync<bool>(
                "SELECT ES_DUPLICADO FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = duplicate.ReceptionId });
            Assert.True(esDuplicado);
            var hashAsociado = await connection.ExecuteScalarAsync<string>(
                "SELECT HASH_TICKET_ASOCIADO FROM dbo.TICKET_RECEPCION WHERE ID_RECEPCION = @Id",
                new { Id = duplicate.ReceptionId });
            Assert.Equal(hash, hashAsociado);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    [SqlFact]
    public async Task Violacion_UX_TICKET_HASH_se_reconoce_y_hace_rollback()
    {
        var processor = SqlTestEnvironment.CreateProcessor();
        var xml = FixtureFile.ReadReal("Fact_B29189644_1_52-2-1_20260815_100759_3.00_sin_firmar.xml");
        var num = "U" + Guid.NewGuid().ToString("N")[..12];
        var uniqueXml = xml.Replace("<NumFactura>1</NumFactura>", $"<NumFactura>{num}</NumFactura>");
        var fileName = $"Fact_B29189644_1_52-2-{num}_20260815_100759_3.00_sin_firmar.xml";
        var command = SqlTestEnvironment.CreateCommand(uniqueXml, fileName);
        var hash = Sha256FileHasher.ComputeHex(command.FileBytes);
        var cleanup = SqlTestEnvironment.BeginCleanup(command);

        var factory = new SqlConnectionFactory(SqlTestEnvironment.ConnectionString);
        var receptions = new ReceptionRepository();
        var tickets = new TicketRepository();
        await using var connection = factory.Create();
        await connection.OpenAsync();
        try
        {
            var first = await processor.ImportAsync(command);
            cleanup.Track(first);
            Assert.NotNull(first.TicketId);

            var mapped = TicketSqlMapper.Map(new TicketXmlParser().Parse(uniqueXml).Ticket!);
            var receptionId = await receptions.InsertAsync(
                connection,
                null,
                new ReceptionInsert
                {
                    FechaRecepcion = DateTime.Now,
                    NombreFichero = "carrera.xml",
                    RutaOrigen = @"C:\DbInda\Entrada\carrera.xml",
                    HashSha256 = hash,
                    TamanoBytes = uniqueXml.Length,
                    Estado = ReceptionStatuses.Pendiente,
                    FechaPrimerIntento = DateTime.Now,
                    FechaUltimoIntento = DateTime.Now,
                    XsdValido = false,
                    EstadoValidacionXsd = XsdValidationStatuses.InvalidoIncompatibilidadConocida
                },
                CancellationToken.None);
            cleanup.TrackReception(receptionId);

            await using var transaction = await connection.BeginTransactionAsync();
            var caught = false;
            try
            {
                await tickets.InsertGraphAsync(
                    connection,
                    transaction,
                    mapped,
                    receptionId,
                    hash,
                    TicketQualityStatuses.Ok,
                    DateTime.Now,
                    0,
                    CancellationToken.None);
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                caught = true;
                Assert.True(SqlUniqueConstraint.IsTicketHashDuplicate(ex), ex.Message);
                await transaction.RollbackAsync();
            }

            Assert.True(caught);
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.TICKET WHERE HASH_SHA256 = @Hash",
                new { Hash = hash });
            Assert.Equal(1, count);
        }
        finally
        {
            await cleanup.DeleteOwnedAsync();
        }
    }

    private static TicketWriteModel CloneWithInvalidLine(TicketWriteModel write)
        => new()
        {
            Source = write.Source,
            Warnings = write.Warnings,
            NifEmisor = write.NifEmisor,
            RazonSocialEmisor = write.RazonSocialEmisor,
            SerieFactura = write.SerieFactura,
            NumFactura = write.NumFactura,
            FechaExpedicion = write.FechaExpedicion,
            HoraExpedicion = write.HoraExpedicion,
            Tienda = write.Tienda,
            Tpv = write.Tpv,
            NVendedor = write.NVendedor,
            DVendedor = write.DVendedor,
            NFormaPago = write.NFormaPago,
            DFormaPago = write.DFormaPago,
            ImporteTotal = write.ImporteTotal,
            FacturaSimplificada = write.FacturaSimplificada,
            FacturaSustitucionSimplificada = write.FacturaSustitucionSimplificada,
            EmitidaPor = write.EmitidaPor,
            DescripcionFactura = write.DescripcionFactura,
            FechaOperacion = write.FechaOperacion,
            RetencionSoportada = write.RetencionSoportada,
            BaseImponibleACoste = write.BaseImponibleACoste,
            IdVersionTbai = write.IdVersionTbai,
            NEncargo = write.NEncargo,
            IdSala = write.IdSala,
            TipoMesa = write.TipoMesa,
            IdMesa = write.IdMesa,
            IdClient = write.IdClient,
            NumSerieDispositivo = write.NumSerieDispositivo,
            SerieFacturaAnterior = write.SerieFacturaAnterior,
            NumFacturaAnterior = write.NumFacturaAnterior,
            FechaFacturaAnterior = write.FechaFacturaAnterior,
            HashFirmaFacturaAnterior = write.HashFirmaFacturaAnterior,
            Details =
            [
                new DetailWriteModel
                {
                    NumLinea = 0,
                    Descripcion = "rollback",
                    Cantidad = 1m,
                    ImporteUnitario = 0m,
                    ImporteTotal = 1m
                }
            ],
            VatBreakdowns = write.VatBreakdowns,
            TaxKeys = write.TaxKeys,
            Recipients = write.Recipients,
            Rectifications = write.Rectifications
        };
}
