using Dapper;
using Microsoft.Data.SqlClient;
using DbInda.Worker.Persistence;

namespace DbInda.Worker.Orders;

public sealed class PedidoRepository : IPedidoRepository
{
    private readonly SqlConnectionFactory _connections;
    private readonly ILogger<PedidoRepository> _logger;

    public PedidoRepository(SqlConnectionFactory connections, ILogger<PedidoRepository> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task<PedidoReceptionRow?> FindByHashAsync(string hash, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                ID_RECEPCION AS IdRecepcion,
                DS_ESTADO AS Estado,
                ID_PEDIDO AS IdPedido,
                DS_RUTA_FINAL AS RutaFinal
            FROM dbo.PEDIDO_TPV_RECEPCION
            WHERE DS_HASH_SHA256 = @Hash;
            """;

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PedidoReceptionRow>(new CommandDefinition(
            sql,
            new { Hash = hash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PedidoSaveResult> SaveAsync(PedidoSaveRequest request, CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (PedidoSqlErrors.IsTransient(ex))
        {
            return PedidoSaveResult.Transient(ex.Message);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.Now;
            var receptionId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO dbo.PEDIDO_TPV_RECEPCION (
                    DS_NOMBRE_FICHERO, DS_RUTA_ORIGEN, DS_RUTA_FINAL, DS_HASH_SHA256, NU_TAMANO_BYTES,
                    DS_ESTADO, NU_INTENTOS, FH_RECEPCION, FH_INICIO_PROCESADO, FH_FIN_PROCESADO,
                    DS_ERROR, FH_ARCHIVO, CD_TIENDA_ARCHIVO, CD_CAJA_ARCHIVO, ID_PEDIDO)
                OUTPUT INSERTED.ID_RECEPCION
                VALUES (
                    @FileName, @SourcePath, @FinalPath, @Hash, @Size,
                    @Estado, 1, @Now, @Now, NULL,
                    NULL, @FileDate, @Store, @Cash, NULL);
                """,
                new
                {
                    request.FileName,
                    request.SourcePath,
                    request.FinalPath,
                    request.Hash,
                    request.Size,
                    Estado = PedidoReceptionStatuses.Procesando,
                    Now = now,
                    FileDate = request.Header.OrderedAt.Date,
                    Store = request.StoreFromName,
                    Cash = request.CashFromName
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var header = request.Header;
            var orderId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO dbo.PEDIDO_TPV (
                    ID_RECEPCION, ID_TIENDA, ALMACEN, ID_CAJA, ID_USUARIO, ORDRELINIA, FH_PEDIDO,
                    DATAASERVIR, HORAASERVIR, TIPO_MESA, ESENCARGO, ID_CLIENTENV, ID_EMPRESA, ID_SALA, ID_TAULA,
                    NU_LINEAS, FH_IMPORTACION)
                OUTPUT INSERTED.ID_PEDIDO
                VALUES (
                    @ReceptionId, @StoreId, @WarehouseId, @CashRegisterId, @UserId, @OrderNumber, @OrderedAt,
                    @ServeDate, @ServeTime, @TableType, @EsEncargo, @DeliveryClientId, @CompanyId, @RoomId, @TableId,
                    @LineCount, @ImportedAt);
                """,
                new
                {
                    ReceptionId = receptionId,
                    header.StoreId,
                    header.WarehouseId,
                    header.CashRegisterId,
                    header.UserId,
                    header.OrderNumber,
                    header.OrderedAt,
                    header.ServeDate,
                    header.ServeTime,
                    header.TableType,
                    header.EsEncargo,
                    header.DeliveryClientId,
                    header.CompanyId,
                    header.RoomId,
                    header.TableId,
                    header.LineCount,
                    ImportedAt = now
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            const string detailSql = """
                INSERT INTO dbo.PEDIDO_TPV_DETALLE (
                    ID_PEDIDO, NU_LINEA_FICHERO, ID_TIQUETL, ID_TIENDA, ALMACEN, ID_CAIXA, ID_USUARI,
                    TIEMPO, COMISION, ID_TARJACLIENT, ID_TAULA, ID_SALA, ID_EMPRESA, ID_ARQUEIG,
                    ID_ARTICLE, ID_IVA, ID_TARIFA, ID_FORMAT, DESCRIPCIO, QUANTITAT, MESURA, ESPERPES, NUMPERSONES,
                    PVC, DESCOMPTE, PREUPERPES, PREU, PREUAMBIVA, PREUTARIFA, PERCENTIVA, PERCENTREQ,
                    HORA_INICI, COMENTARIS, OBSERVACIO, BASE, TOTALDTE, TOTAL, CVALIVA, CVALREQ,
                    DATAASERVIR, HORAASERVIR, ID_PROVEEDOR, ESENCARGO, ID_CLIENTENV, ID_ARTICLETC,
                    ID_COLOR, DESCCOLOR, DESCMARCA, TALLA, ID_MARCA, FOTOMARCA, OCULTARCTD, NOIMPRIMIR,
                    ID_TIQUETLMASTER, JAIMPRESACUINA, PRINTACUINA, PRINTABARRA, TEMODIFICADORS, TEOPCIONSKIT,
                    ESKIT, ESMODIFICADOR, ORDRELINIA, TIPO_MESA, ESIMPRESO, ID_ARTCENTRAL, ID_FAMILIA, MARCADO, ID_INTERNET)
                VALUES (
                    @OrderId, @LineNumber, @IdTiquet, @StoreId, @WarehouseId, @CashRegisterId, @UserId,
                    @Tiempo, @Comision, @ClientCardId, @TableId, @RoomId, @CompanyId, @CashCountId,
                    @ArticleId, @VatId, @TariffId, @FormatId, @Description, @Quantity, @Measure, @EsPerPes, @PersonCount,
                    @Pvc, @Discount, @PricePerWeight, @Price, @PriceWithVat, @TariffPrice, @VatPercent, @SurchargePercent,
                    @StartTime, @Comments, @Observation, @BaseAmount, @DiscountTotal, @Total, @VatAmount, @SurchargeAmount,
                    @ServeDate, @ServeTime, @SupplierId, @EsEncargo, @DeliveryClientId, @ArticleTcId,
                    @ColorId, @ColorDescription, @BrandDescription, @Size, @BrandId, @BrandPhoto, @HideQuantity, @DoNotPrint,
                    @MasterTicketId, @AlreadyPrintedKitchen, @PrintKitchen, @PrintBar, @HasModifiers, @HasKitOptions,
                    @EsKit, @EsModificador, @OrderNumber, @TableType, @EsImpreso, @CentralArticleId, @FamilyId, @Marked, @InternetId);
                """;

            foreach (var line in request.Lines)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    detailSql,
                    new
                    {
                        OrderId = orderId,
                        line.LineNumber,
                        line.IdTiquet,
                        line.StoreId,
                        line.WarehouseId,
                        line.CashRegisterId,
                        line.UserId,
                        line.Tiempo,
                        line.Comision,
                        line.ClientCardId,
                        line.TableId,
                        line.RoomId,
                        line.CompanyId,
                        line.CashCountId,
                        line.ArticleId,
                        line.VatId,
                        line.TariffId,
                        line.FormatId,
                        line.Description,
                        line.Quantity,
                        line.Measure,
                        line.EsPerPes,
                        line.PersonCount,
                        line.Pvc,
                        line.Discount,
                        line.PricePerWeight,
                        line.Price,
                        line.PriceWithVat,
                        line.TariffPrice,
                        line.VatPercent,
                        line.SurchargePercent,
                        line.StartTime,
                        line.Comments,
                        line.Observation,
                        line.BaseAmount,
                        line.DiscountTotal,
                        line.Total,
                        line.VatAmount,
                        line.SurchargeAmount,
                        line.ServeDate,
                        line.ServeTime,
                        line.SupplierId,
                        line.EsEncargo,
                        line.DeliveryClientId,
                        line.ArticleTcId,
                        line.ColorId,
                        line.ColorDescription,
                        line.BrandDescription,
                        line.Size,
                        line.BrandId,
                        line.BrandPhoto,
                        line.HideQuantity,
                        line.DoNotPrint,
                        line.MasterTicketId,
                        line.AlreadyPrintedKitchen,
                        line.PrintKitchen,
                        line.PrintBar,
                        line.HasModifiers,
                        line.HasKitOptions,
                        line.EsKit,
                        line.EsModificador,
                        line.OrderNumber,
                        line.TableType,
                        line.EsImpreso,
                        line.CentralArticleId,
                        line.FamilyId,
                        line.Marked,
                        line.InternetId
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.PEDIDO_TPV_RECEPCION
                SET DS_ESTADO = @Estado,
                    ID_PEDIDO = @OrderId,
                    FH_FIN_PROCESADO = @Now,
                    DS_RUTA_FINAL = @FinalPath
                WHERE ID_RECEPCION = @ReceptionId;
                """,
                new
                {
                    Estado = PedidoReceptionStatuses.Procesado,
                    OrderId = orderId,
                    Now = now,
                    request.FinalPath,
                    ReceptionId = receptionId
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return PedidoSaveResult.Saved(orderId, receptionId);
        }
        catch (Exception ex) when (PedidoSqlErrors.IsHashDuplicate(ex))
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return PedidoSaveResult.Duplicate();
        }
        catch (Exception ex) when (PedidoSqlErrors.IsTransient(ex))
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return PedidoSaveResult.Transient(ex.Message);
        }
        catch (SqlException ex)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return PedidoSaveResult.Permanent(ex.Message);
        }
    }

    public async Task<PedidoErrorWrite> TryInsertErrorAsync(
        string fileName,
        string sourcePath,
        string hash,
        long size,
        int? storeFromName,
        int? cashFromName,
        DateTime? fileDate,
        string error,
        CancellationToken cancellationToken)
    {
        var message = error.Length <= 2000 ? error : error[..2000];
        await using var connection = _connections.Create();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO dbo.PEDIDO_TPV_RECEPCION (
                    DS_NOMBRE_FICHERO, DS_RUTA_ORIGEN, DS_RUTA_FINAL, DS_HASH_SHA256, NU_TAMANO_BYTES,
                    DS_ESTADO, NU_INTENTOS, FH_RECEPCION, FH_INICIO_PROCESADO, FH_FIN_PROCESADO,
                    DS_ERROR, FH_ARCHIVO, CD_TIENDA_ARCHIVO, CD_CAJA_ARCHIVO, ID_PEDIDO)
                VALUES (
                    @FileName, @SourcePath, NULL, @Hash, @Size,
                    @Estado, 1, @Now, @Now, @Now,
                    @Error, @FileDate, @Store, @Cash, NULL);
                """,
                new
                {
                    FileName = fileName,
                    SourcePath = sourcePath,
                    Hash = hash,
                    Size = size,
                    Estado = PedidoReceptionStatuses.Error,
                    Now = DateTime.Now,
                    Error = message,
                    FileDate = fileDate,
                    Store = storeFromName,
                    Cash = cashFromName
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return PedidoErrorWrite.Recorded;
        }
        catch (Exception ex) when (PedidoSqlErrors.IsHashDuplicate(ex))
        {
            return PedidoErrorWrite.AlreadyKnown;
        }
        catch (Exception ex) when (PedidoSqlErrors.IsTransient(ex))
        {
            return PedidoErrorWrite.Transient;
        }
    }

    public async Task TryUpdateFinalPathAsync(long receptionId, string finalPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.PEDIDO_TPV_RECEPCION
                SET DS_RUTA_FINAL = @FinalPath
                WHERE ID_RECEPCION = @ReceptionId;
                """,
                new { FinalPath = finalPath, ReceptionId = receptionId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (PedidoSqlErrors.IsTransient(ex) || ex is SqlException)
        {
            _logger.LogWarning(ex, "No se pudo actualizar la ruta final del pedido. ReceptionId={ReceptionId}", receptionId);
        }
    }

    private async Task RollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo deshacer la transacción del pedido.");
        }
    }
}
