namespace DbInda.Tests;

internal static class FixtureFile
{
    public static string RealDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Real");
    public static string SyntheticDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Synthetic");
    public static string XsdDirectory => Path.Combine(AppContext.BaseDirectory, "xsd");

    public static string ReadReal(string fileName)
        => File.ReadAllText(Path.Combine(RealDirectory, fileName));

    public static string ReadSynthetic(string fileName)
        => File.ReadAllText(Path.Combine(SyntheticDirectory, fileName));

    public static string RealPath(string fileName)
        => Path.Combine(RealDirectory, fileName);

    public static IReadOnlyList<string> RealFileNames()
        => Directory.GetFiles(RealDirectory, "*.xml").Select(Path.GetFileName).Where(n => n is not null).Cast<string>().OrderBy(n => n).ToArray();
}

internal static class TicketBaiSkeleton
{
    public static string WrapFactura(
        string extraCabeceraFactura = "",
        string extraSujetos = "",
        string extraCabecera = "",
        string detalles = DefaultDetalle,
        string claves = DefaultClave,
        string extraDatosFactura = "",
        string desglose = DefaultDesglose,
        string numFactura = "1",
        string fecha = "15-08-2026",
        string cantidad = "1.000")
    {
        var detalleXml = detalles == DefaultDetalle
            ? DefaultDetalle.Replace("{CANTIDAD}", cantidad)
            : detalles;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <T:TicketBai xmlns:T="urn:ticketbai:emision">
            <Cabecera>
            <IDVersionTBAI>1.2</IDVersionTBAI>
            {extraCabecera}
            </Cabecera>
            <Sujetos>
            <Emisor>
            <NIF>B29189644</NIF>
            <ApellidosNombreRazonSocial>CIAL DE PANADERA Y CONF., S.L.</ApellidosNombreRazonSocial>
            </Emisor>
            {extraSujetos}
            </Sujetos>
            <Factura>
            <CabeceraFactura>
            <SerieFactura>52.2.1</SerieFactura>
            <NumFactura>{numFactura}</NumFactura>
            <FechaExpedicionFactura>{fecha}</FechaExpedicionFactura>
            <HoraExpedicionFactura>10:07:59</HoraExpedicionFactura>
            {extraCabeceraFactura}
            </CabeceraFactura>
            <DatosFactura>
            <DescripcionFactura>Venta</DescripcionFactura>
            <DetallesFactura>
            {detalleXml}
            </DetallesFactura>
            <ImporteTotalFactura>3.00</ImporteTotalFactura>
            {extraDatosFactura}
            <Claves>
            {claves}
            </Claves>
            </DatosFactura>
            <TipoDesglose>
            {desglose}
            </TipoDesglose>
            </Factura>
            <HuellaTBAI>
            <Software>
            <LicenciaTBAI>TPVONE</LicenciaTBAI>
            <EntidadDesarrolladora>
            <NIF>A29101995</NIF>
            </EntidadDesarrolladora>
            <Nombre>TEST</Nombre>
            <Version>1.0</Version>
            </Software>
            <NumSerieDispositivo>644522250725084342</NumSerieDispositivo>
            </HuellaTBAI>
            </T:TicketBai>
            """;
    }

    public const string DefaultClave = """
        <IDClave>
        <ClaveRegimenIvaOpTrascendencia>01</ClaveRegimenIvaOpTrascendencia>
        </IDClave>
        """;

    public const string DefaultDesglose = """
        <DesgloseFactura>
        <Sujeta>
        <NoExenta>
        <DetalleNoExenta>
        <TipoNoExenta>S1</TipoNoExenta>
        <DesgloseIVA>
        <DetalleIVA>
        <BaseImponible>2.73</BaseImponible>
        <TipoImpositivo>10.00</TipoImpositivo>
        <CuotaImpuesto>0.27</CuotaImpuesto>
        </DetalleIVA>
        </DesgloseIVA>
        </DetalleNoExenta>
        </NoExenta>
        </Sujeta>
        </DesgloseFactura>
        """;

    public const string DefaultDetalle = """
        <IDDetalleFactura>
        <DescripcionDetalle>PRUEBA</DescripcionDetalle>
        <Cantidad>{CANTIDAD}</Cantidad>
        <ImporteUnitario>0.00000000</ImporteUnitario>
        <ImporteTotal>3.00</ImporteTotal>
        </IDDetalleFactura>
        """;
}
