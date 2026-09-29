namespace DbInda.Worker.Orders;

public static class PedidoColumns
{
    // The TPV writes 66 names and then a trailing '|', so Split yields 67 fields.
    // Field 67 has no name and is empty. It does not shift the indexes of the named columns.
    public static readonly string[] Header =
    [
        "ID_TIQUETL",
        "ID_tienda",
        "almacen",
        "ID_CAIXA",
        "ID_USUARI",
        "tiempo",
        "comision",
        "ID_TARJACLIENT",
        "ID_TAULA",
        "ID_SALA",
        "ID_EMPRESA",
        "ID_ARQUEIG",
        "ID_ARTICLE",
        "ID_IVA",
        "ID_TARIFA",
        "ID_FORMAT",
        "DESCRIPCIO",
        "QUANTITAT",
        "MESURA",
        "ESPERPES",
        "NUMPERSONES",
        "PVC",
        "DESCOMPTE",
        "PREUPERPES",
        "PREU",
        "PREUAMBIVA",
        "PREUTARIFA",
        "PERCENTIVA",
        "PERCENTREQ",
        "HORA_INICI",
        "COMENTARIS",
        "OBSERVACIO",
        "BASE",
        "TOTALDTE",
        "TOTAL",
        "CVALIVA",
        "CVALREQ",
        "DATAASERVIR",
        "HORAASERVIR",
        "ID_proveedor",
        "ESENCARGO",
        "ID_CLIENTENV",
        "ID_ARTICLETC",
        "ID_COLOR",
        "DESCCOLOR",
        "DESCMARCA",
        "TALLA",
        "ID_MARCA",
        "FOTOMARCA",
        "OCULTARCTD",
        "NOIMPRIMIR",
        "ID_TIQUETLMASTER",
        "JAIMPRESACUINA",
        "PRINTACUINA",
        "PRINTABARRA",
        "TEMODIFICADORS",
        "TEOPCIONSKIT",
        "ESKIT",
        "ESMODIFICADOR",
        "ORDRELINIA",
        "tipo_mesa",
        "esimpreso",
        "id_artcentral",
        "id_familia",
        "marcado",
        "id_internet"
    ];

    public const int NamedCount = 66;
    public const int Count = 67;

    public static int Index(string name)
    {
        for (var i = 0; i < Header.Length; i++)
        {
            if (string.Equals(Header[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
