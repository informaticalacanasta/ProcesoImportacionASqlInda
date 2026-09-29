namespace DbInda.Worker.Orders;

public sealed class PedidoTxtParser
{
    public PedidoParseResult Parse(string fileName, string text)
    {
        if (!PedidoFileClassifier.TryMatch(fileName, out var identity))
            return PedidoParseResult.Fail("El nombre no corresponde a un pedido PEDIDOS_TMPP.");
        if (identity.OrderedAt is null)
            return PedidoParseResult.Fail("El sello del nombre no es una fecha FH_PEDIDO válida.");

        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var physical = normalized.Split('\n');
        string? headerLine = null;
        var headerNumber = 0;
        for (var i = 0; i < physical.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(physical[i]))
                continue;
            headerLine = physical[i];
            headerNumber = i + 1;
            break;
        }

        if (headerLine is null)
            return PedidoParseResult.Fail("El fichero no tiene cabecera.");

        var headerError = ValidateHeader(headerLine, headerNumber);
        if (headerError is not null)
            return PedidoParseResult.Fail(headerError);

        var lines = new List<PedidoDetail>();
        PedidoDetail? first = null;
        for (var i = headerNumber; i < physical.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(physical[i]))
                continue;
            var lineNumber = i + 1;
            PedidoDetail detail;
            try
            {
                detail = ReadDetail(physical[i], lineNumber);
            }
            catch (FormatException ex)
            {
                return PedidoParseResult.Fail(ex.Message);
            }

            if (first is null)
                first = detail;
            else if (detail.StoreId != first.StoreId || detail.CashRegisterId != first.CashRegisterId || detail.OrderNumber != first.OrderNumber)
                return PedidoParseResult.Fail($"La línea {lineNumber} no coincide en ID_tienda, ID_CAIXA u ORDRELINIA con la primera línea de datos.");

            lines.Add(detail);
        }

        if (first is null)
            return PedidoParseResult.Fail("El pedido no tiene líneas de datos.");
        if (first.StoreId is null || first.CashRegisterId is null || first.OrderNumber is null)
            return PedidoParseResult.Fail("ID_tienda, ID_CAIXA y ORDRELINIA son obligatorios.");
        if (first.StoreId != identity.StoreId)
            return PedidoParseResult.Fail($"ID_tienda {first.StoreId} no coincide con la tienda {identity.StoreId} del nombre.");
        if (first.CashRegisterId != identity.CashRegisterId)
            return PedidoParseResult.Fail($"ID_CAIXA {first.CashRegisterId} no coincide con la caja {identity.CashRegisterId} del nombre.");

        var header = new PedidoHeader
        {
            StoreId = first.StoreId.Value,
            WarehouseId = first.WarehouseId,
            CashRegisterId = first.CashRegisterId.Value,
            UserId = first.UserId,
            OrderNumber = first.OrderNumber.Value,
            OrderedAt = identity.OrderedAt.Value,
            ServeDate = first.ServeDate,
            ServeTime = first.ServeTime,
            TableType = first.TableType,
            EsEncargo = first.EsEncargo,
            DeliveryClientId = first.DeliveryClientId,
            CompanyId = first.CompanyId,
            RoomId = first.RoomId,
            TableId = first.TableId,
            LineCount = lines.Count
        };

        return PedidoParseResult.Ok(new PedidoDocument
        {
            Identity = identity,
            Header = header,
            Lines = lines
        });
    }

    private static string? ValidateHeader(string line, int lineNumber)
    {
        var cells = Split(line);
        if (cells.Length != PedidoColumns.Count)
            return $"La cabecera (línea {lineNumber}) tiene {cells.Length} columnas y se esperaban {PedidoColumns.Count}.";
        for (var i = 0; i < PedidoColumns.Header.Length; i++)
        {
            if (!string.Equals(cells[i].Trim(), PedidoColumns.Header[i], StringComparison.OrdinalIgnoreCase))
                return $"La columna {i + 1} de la cabecera es '{cells[i].Trim()}' y se esperaba '{PedidoColumns.Header[i]}'.";
        }

        if (!string.IsNullOrWhiteSpace(cells[PedidoColumns.Header.Length]))
            return $"La columna {PedidoColumns.Count} de la cabecera no tiene nombre y trae '{cells[PedidoColumns.Header.Length].Trim()}'.";

        return null;
    }

    private static PedidoDetail ReadDetail(string line, int lineNumber)
    {
        var cells = Split(line);
        if (cells.Length != PedidoColumns.Count)
            throw new FormatException($"La línea {lineNumber} tiene {cells.Length} columnas y se esperaban {PedidoColumns.Count}.");
        if (!string.IsNullOrWhiteSpace(cells[PedidoColumns.Header.Length]))
            throw new FormatException($"La línea {lineNumber} trae un valor en la columna {PedidoColumns.Count}, que no tiene nombre.");

        return new PedidoDetail
        {
            LineNumber = lineNumber,
            IdTiquet = Text(cells, "ID_TIQUETL", lineNumber),
            StoreId = Int(cells, "ID_tienda", lineNumber),
            WarehouseId = Int(cells, "almacen", lineNumber),
            CashRegisterId = Int(cells, "ID_CAIXA", lineNumber),
            UserId = Int(cells, "ID_USUARI", lineNumber),
            Tiempo = Text(cells, "tiempo", lineNumber),
            Comision = Decimal(cells, "comision", lineNumber),
            ClientCardId = Text(cells, "ID_TARJACLIENT", lineNumber),
            TableId = Int(cells, "ID_TAULA", lineNumber),
            RoomId = Int(cells, "ID_SALA", lineNumber),
            CompanyId = Int(cells, "ID_EMPRESA", lineNumber),
            CashCountId = Text(cells, "ID_ARQUEIG", lineNumber),
            ArticleId = Text(cells, "ID_ARTICLE", lineNumber),
            VatId = Text(cells, "ID_IVA", lineNumber),
            TariffId = Text(cells, "ID_TARIFA", lineNumber),
            FormatId = Text(cells, "ID_FORMAT", lineNumber),
            Description = Text(cells, "DESCRIPCIO", lineNumber),
            Quantity = Decimal(cells, "QUANTITAT", lineNumber),
            Measure = Decimal(cells, "MESURA", lineNumber),
            EsPerPes = Bool(cells, "ESPERPES", lineNumber),
            PersonCount = Int(cells, "NUMPERSONES", lineNumber),
            Pvc = Decimal(cells, "PVC", lineNumber),
            Discount = Decimal(cells, "DESCOMPTE", lineNumber),
            PricePerWeight = Decimal(cells, "PREUPERPES", lineNumber),
            Price = Decimal(cells, "PREU", lineNumber),
            PriceWithVat = Decimal(cells, "PREUAMBIVA", lineNumber),
            TariffPrice = Decimal(cells, "PREUTARIFA", lineNumber),
            VatPercent = Decimal(cells, "PERCENTIVA", lineNumber),
            SurchargePercent = Decimal(cells, "PERCENTREQ", lineNumber),
            StartTime = Time(cells, "HORA_INICI", lineNumber),
            Comments = Text(cells, "COMENTARIS", lineNumber),
            Observation = Text(cells, "OBSERVACIO", lineNumber),
            BaseAmount = Decimal(cells, "BASE", lineNumber),
            DiscountTotal = Decimal(cells, "TOTALDTE", lineNumber),
            Total = Decimal(cells, "TOTAL", lineNumber),
            VatAmount = Decimal(cells, "CVALIVA", lineNumber),
            SurchargeAmount = Decimal(cells, "CVALREQ", lineNumber),
            ServeDate = Date(cells, "DATAASERVIR", lineNumber),
            ServeTime = Time(cells, "HORAASERVIR", lineNumber),
            SupplierId = Text(cells, "ID_proveedor", lineNumber),
            EsEncargo = Bool(cells, "ESENCARGO", lineNumber),
            DeliveryClientId = Int(cells, "ID_CLIENTENV", lineNumber),
            ArticleTcId = Text(cells, "ID_ARTICLETC", lineNumber),
            ColorId = Text(cells, "ID_COLOR", lineNumber),
            ColorDescription = Text(cells, "DESCCOLOR", lineNumber),
            BrandDescription = Text(cells, "DESCMARCA", lineNumber),
            Size = Text(cells, "TALLA", lineNumber),
            BrandId = Text(cells, "ID_MARCA", lineNumber),
            BrandPhoto = Text(cells, "FOTOMARCA", lineNumber),
            HideQuantity = Bool(cells, "OCULTARCTD", lineNumber),
            DoNotPrint = Bool(cells, "NOIMPRIMIR", lineNumber),
            MasterTicketId = Text(cells, "ID_TIQUETLMASTER", lineNumber),
            AlreadyPrintedKitchen = Bool(cells, "JAIMPRESACUINA", lineNumber),
            PrintKitchen = Bool(cells, "PRINTACUINA", lineNumber),
            PrintBar = Bool(cells, "PRINTABARRA", lineNumber),
            HasModifiers = Bool(cells, "TEMODIFICADORS", lineNumber),
            HasKitOptions = Bool(cells, "TEOPCIONSKIT", lineNumber),
            EsKit = Bool(cells, "ESKIT", lineNumber),
            EsModificador = Bool(cells, "ESMODIFICADOR", lineNumber),
            OrderNumber = Long(cells, "ORDRELINIA", lineNumber),
            TableType = Text(cells, "tipo_mesa", lineNumber),
            EsImpreso = Bool(cells, "esimpreso", lineNumber),
            CentralArticleId = Text(cells, "id_artcentral", lineNumber),
            FamilyId = Text(cells, "id_familia", lineNumber),
            Marked = Bool(cells, "marcado", lineNumber),
            InternetId = Text(cells, "id_internet", lineNumber)
        };
    }

    private static string[] Split(string line) => line.Split('|');

    private static string Cell(string[] cells, string name) => cells[PedidoColumns.Index(name)];

    private static string? Text(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Text(Cell(cells, name)));

    private static int? Int(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Int32(Cell(cells, name)));

    private static long? Long(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Int64(Cell(cells, name)));

    private static decimal? Decimal(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Decimal(Cell(cells, name)));

    private static bool? Bool(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Boolean(Cell(cells, name)));

    private static DateTime? Date(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Date(Cell(cells, name)));

    private static TimeSpan? Time(string[] cells, string name, int line)
        => Field(name, line, () => PedidoValues.Time(Cell(cells, name)));

    private static T Field<T>(string name, int line, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (FormatException)
        {
            throw new FormatException($"La línea {line} tiene un valor no válido en {name}.");
        }
    }
}
