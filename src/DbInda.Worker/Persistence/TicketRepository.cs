using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace DbInda.Worker.Persistence;

public sealed class TicketRepository
{
    public async Task<ExistingTicketRef?> FindByHashAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        string hashSha256,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                ID_TICKET AS IdTicket,
                ID_RECEPCION_ORIGEN AS IdRecepcionOrigen,
                HASH_SHA256 AS HashSha256
            FROM dbo.TICKET
            WHERE HASH_SHA256 = @HashSha256;
            """;

        return await connection.QuerySingleOrDefaultAsync<ExistingTicketRef>(
            new CommandDefinition(sql, new { HashSha256 = hashSha256 }, transaction, cancellationToken: cancellationToken));
    }

    public async Task<ExistingTicketRef?> FindByIdentityAsync(
        SqlConnection connection,
        IDbTransaction? transaction,
        string nifEmisor,
        string? serieFactura,
        string numFactura,
        DateOnly fechaExpedicion,
        int? tienda,
        int? tpv,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                ID_TICKET AS IdTicket,
                ID_RECEPCION_ORIGEN AS IdRecepcionOrigen,
                HASH_SHA256 AS HashSha256
            FROM dbo.TICKET
            WHERE NIF_EMISOR = @NifEmisor
              AND SERIE_FACTURA_NORM = CAST(ISNULL(@SerieFactura, '') AS VARCHAR(20))
              AND NUM_FACTURA = @NumFactura
              AND FECHA_EXPEDICION = @FechaExpedicion
              AND ((TIENDA IS NULL AND @Tienda IS NULL) OR TIENDA = @Tienda)
              AND ((TPV IS NULL AND @Tpv IS NULL) OR TPV = @Tpv)
            ORDER BY ID_TICKET;
            """;

        return await connection.QuerySingleOrDefaultAsync<ExistingTicketRef>(
            new CommandDefinition(
                sql,
                new
                {
                    NifEmisor = nifEmisor,
                    SerieFactura = serieFactura,
                    NumFactura = numFactura,
                    FechaExpedicion = SqlTemporal.ToDbDate(fechaExpedicion),
                    Tienda = tienda,
                    Tpv = tpv
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<long> InsertGraphAsync(
        SqlConnection connection,
        IDbTransaction transaction,
        TicketWriteModel ticket,
        long idRecepcionOrigen,
        string hashSha256,
        string estadoCalidad,
        DateTime fechaAlta,
        int numeroWarnings,
        CancellationToken cancellationToken)
    {
        var idTicket = await InsertTicketAsync(
            connection, transaction, ticket, idRecepcionOrigen, hashSha256, estadoCalidad, fechaAlta, numeroWarnings, cancellationToken);

        foreach (var detail in ticket.Details)
            await InsertDetailAsync(connection, transaction, idTicket, detail, cancellationToken);
        foreach (var vat in ticket.VatBreakdowns)
            await InsertVatAsync(connection, transaction, idTicket, vat, cancellationToken);
        foreach (var key in ticket.TaxKeys)
            await InsertKeyAsync(connection, transaction, idTicket, key, cancellationToken);
        foreach (var recipient in ticket.Recipients)
            await InsertRecipientAsync(connection, transaction, idTicket, recipient, cancellationToken);
        foreach (var rectification in ticket.Rectifications)
            await InsertRectificationAsync(connection, transaction, idTicket, rectification, cancellationToken);

        return idTicket;
    }

    private static async Task<long> InsertTicketAsync(
        SqlConnection connection,
        IDbTransaction transaction,
        TicketWriteModel ticket,
        long idRecepcionOrigen,
        string hashSha256,
        string estadoCalidad,
        DateTime fechaAlta,
        int numeroWarnings,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.TICKET (
                ID_RECEPCION_ORIGEN, HASH_SHA256, FECHA_ALTA, ESTADO_CALIDAD,
                NIF_EMISOR, RAZON_SOCIAL_EMISOR, SERIE_FACTURA, NUM_FACTURA,
                FECHA_EXPEDICION, HORA_EXPEDICION, TIENDA, TPV,
                N_VENDEDOR, D_VENDEDOR, N_FORMA_PAGO, D_FORMA_PAGO, IMPORTE_TOTAL,
                FACTURA_SIMPLIFICADA, FACTURA_SUSTITUCION_SIMPLIFICADA, EMITIDA_POR,
                DESCRIPCION_FACTURA, FECHA_OPERACION, RETENCION_SOPORTADA, BASE_IMPONIBLE_A_COSTE,
                ID_VERSION_TBAI, N_ENCARGO, ID_SALA, TIPO_MESA, ID_MESA, ID_CLIENT,
                NUM_SERIE_DISPOSITIVO,
                SERIE_FACTURA_ANTERIOR, NUM_FACTURA_ANTERIOR, FECHA_FACTURA_ANTERIOR, HASH_FIRMA_FACTURA_ANTERIOR,
                NUMERO_WARNINGS)
            OUTPUT INSERTED.ID_TICKET
            VALUES (
                @IdRecepcionOrigen, @HashSha256, @FechaAlta, @EstadoCalidad,
                @NifEmisor, @RazonSocialEmisor, @SerieFactura, @NumFactura,
                @FechaExpedicion, @HoraExpedicion, @Tienda, @Tpv,
                @NVendedor, @DVendedor, @NFormaPago, @DFormaPago, @ImporteTotal,
                @FacturaSimplificada, @FacturaSustitucionSimplificada, @EmitidaPor,
                @DescripcionFactura, @FechaOperacion, @RetencionSoportada, @BaseImponibleACoste,
                @IdVersionTbai, @NEncargo, @IdSala, @TipoMesa, @IdMesa, @IdClient,
                @NumSerieDispositivo,
                @SerieFacturaAnterior, @NumFacturaAnterior, @FechaFacturaAnterior, @HashFirmaFacturaAnterior,
                @NumeroWarnings);
            """;

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                IdRecepcionOrigen = idRecepcionOrigen,
                HashSha256 = hashSha256,
                FechaAlta = fechaAlta,
                EstadoCalidad = estadoCalidad,
                ticket.NifEmisor,
                ticket.RazonSocialEmisor,
                ticket.SerieFactura,
                ticket.NumFactura,
                FechaExpedicion = SqlTemporal.ToDbDate(ticket.FechaExpedicion),
                HoraExpedicion = SqlTemporal.ToDbTime(ticket.HoraExpedicion),
                ticket.Tienda,
                ticket.Tpv,
                ticket.NVendedor,
                ticket.DVendedor,
                ticket.NFormaPago,
                ticket.DFormaPago,
                ticket.ImporteTotal,
                ticket.FacturaSimplificada,
                ticket.FacturaSustitucionSimplificada,
                ticket.EmitidaPor,
                ticket.DescripcionFactura,
                FechaOperacion = SqlTemporal.ToDbDate(ticket.FechaOperacion),
                ticket.RetencionSoportada,
                ticket.BaseImponibleACoste,
                ticket.IdVersionTbai,
                ticket.NEncargo,
                ticket.IdSala,
                ticket.TipoMesa,
                ticket.IdMesa,
                ticket.IdClient,
                ticket.NumSerieDispositivo,
                ticket.SerieFacturaAnterior,
                ticket.NumFacturaAnterior,
                FechaFacturaAnterior = SqlTemporal.ToDbDate(ticket.FechaFacturaAnterior),
                ticket.HashFirmaFacturaAnterior,
                NumeroWarnings = numeroWarnings
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Task InsertDetailAsync(
        SqlConnection connection, IDbTransaction transaction, long idTicket, DetailWriteModel d, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TICKET_DETALLE (
                ID_TICKET, NUM_LINEA, DESCRIPCION, CANTIDAD, IMPORTE_UNITARIO, DESCUENTO, IMPORTE_TOTAL,
                CODIGO_CENTRAL, IDENTIFICADOR, FAMILIA, SECCION, FORMATO, ESPERPES, SECCION_SALA,
                PVP_CONSUMO, ES_KIT, ID_TIQUETL_MASTER, ID_TIQUETL, EQUIVALENCIA_UNIDAD, EQUIVALENCIA_PESO,
                PORCENTAJE_IVA, PORCENTAJE_RECARGO)
            VALUES (
                @IdTicket, @NumLinea, @Descripcion, @Cantidad, @ImporteUnitario, @Descuento, @ImporteTotal,
                @CodigoCentral, @Identificador, @Familia, @Seccion, @Formato, @Esperpes, @SeccionSala,
                @PvpConsumo, @EsKit, @IdTiquetlMaster, @IdTiquetl, @EquivalenciaUnidad, @EquivalenciaPeso,
                @PorcentajeIva, @PorcentajeRecargo);
            """,
            new
            {
                IdTicket = idTicket,
                d.NumLinea,
                d.Descripcion,
                d.Cantidad,
                d.ImporteUnitario,
                d.Descuento,
                d.ImporteTotal,
                d.CodigoCentral,
                d.Identificador,
                d.Familia,
                d.Seccion,
                d.Formato,
                d.Esperpes,
                d.SeccionSala,
                d.PvpConsumo,
                d.EsKit,
                d.IdTiquetlMaster,
                d.IdTiquetl,
                d.EquivalenciaUnidad,
                d.EquivalenciaPeso,
                d.PorcentajeIva,
                d.PorcentajeRecargo
            },
            transaction,
            cancellationToken: cancellationToken));

    private static Task InsertVatAsync(
        SqlConnection connection, IDbTransaction transaction, long idTicket, VatWriteModel v, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TICKET_IVA (
                ID_TICKET, NUM_ORDEN, TIPO_DESGLOSE, TIPO_SUJECION, TIPO_NO_EXENTA, CAUSA_EXENCION, CAUSA_NO_SUJETA,
                BASE_IMPONIBLE, TIPO_IMPOSITIVO, CUOTA_IMPUESTO, TIPO_RECARGO_EQUIVALENCIA, CUOTA_RECARGO_EQUIVALENCIA,
                OPERACION_RECARGO_O_SIMPLIFICADO, IMPORTE_NO_SUJETA)
            VALUES (
                @IdTicket, @NumOrden, @TipoDesglose, @TipoSujecion, @TipoNoExenta, @CausaExencion, @CausaNoSujeta,
                @BaseImponible, @TipoImpositivo, @CuotaImpuesto, @TipoRecargoEquivalencia, @CuotaRecargoEquivalencia,
                @OperacionRecargoOSimplificado, @ImporteNoSujeta);
            """,
            new
            {
                IdTicket = idTicket,
                v.NumOrden,
                v.TipoDesglose,
                v.TipoSujecion,
                v.TipoNoExenta,
                v.CausaExencion,
                v.CausaNoSujeta,
                v.BaseImponible,
                v.TipoImpositivo,
                v.CuotaImpuesto,
                v.TipoRecargoEquivalencia,
                v.CuotaRecargoEquivalencia,
                v.OperacionRecargoOSimplificado,
                v.ImporteNoSujeta
            },
            transaction,
            cancellationToken: cancellationToken));

    private static Task InsertKeyAsync(
        SqlConnection connection, IDbTransaction transaction, long idTicket, TaxKeyWriteModel key, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TICKET_CLAVE (ID_TICKET, NUM_ORDEN, CLAVE_REGIMEN_IVA)
            VALUES (@IdTicket, @NumOrden, @ClaveRegimenIva);
            """,
            new { IdTicket = idTicket, key.NumOrden, key.ClaveRegimenIva },
            transaction,
            cancellationToken: cancellationToken));

    private static Task InsertRecipientAsync(
        SqlConnection connection, IDbTransaction transaction, long idTicket, RecipientWriteModel r, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TICKET_DESTINATARIO (
                ID_TICKET, NUM_ORDEN, NIF, CODIGO_PAIS, ID_TYPE, ID_OTRO, APELLIDOS_NOMBRE, CODIGO_POSTAL, DIRECCION)
            VALUES (
                @IdTicket, @NumOrden, @Nif, @CodigoPais, @IdType, @IdOtro, @ApellidosNombre, @CodigoPostal, @Direccion);
            """,
            new
            {
                IdTicket = idTicket,
                r.NumOrden,
                r.Nif,
                r.CodigoPais,
                r.IdType,
                r.IdOtro,
                r.ApellidosNombre,
                r.CodigoPostal,
                r.Direccion
            },
            transaction,
            cancellationToken: cancellationToken));

    private static Task InsertRectificationAsync(
        SqlConnection connection, IDbTransaction transaction, long idTicket, RectificationWriteModel r, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO dbo.TICKET_RECTIFICACION (
                ID_TICKET, NUM_ORDEN, CODIGO, TIPO, BASE_RECTIFICADA, CUOTA_RECTIFICADA, CUOTA_RECARGO_RECTIFICADA,
                SERIE_FACTURA, NUM_FACTURA, FECHA_EXPEDICION)
            VALUES (
                @IdTicket, @NumOrden, @Codigo, @Tipo, @BaseRectificada, @CuotaRectificada, @CuotaRecargoRectificada,
                @SerieFactura, @NumFactura, @FechaExpedicion);
            """,
            new
            {
                IdTicket = idTicket,
                r.NumOrden,
                r.Codigo,
                r.Tipo,
                r.BaseRectificada,
                r.CuotaRectificada,
                r.CuotaRecargoRectificada,
                r.SerieFactura,
                r.NumFactura,
                FechaExpedicion = SqlTemporal.ToDbDate(r.FechaExpedicion)
            },
            transaction,
            cancellationToken: cancellationToken));
}
