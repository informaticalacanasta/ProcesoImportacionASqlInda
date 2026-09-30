using DbInda.Tests.Inbound;
using DbInda.Worker.Files;

namespace DbInda.Tests.Files;

public sealed class TicketDeliveryTests
{
    [Fact]
    public async Task Publica_xml_verificado_y_conserva_original()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(root.Xml("salida/42/ticket.xml")));
        Assert.True(File.Exists(root.Xml("salida/42/manifest.json")));
        Assert.False(Directory.Exists(root.Xml("salida/.staging/42")));
    }

    [Fact]
    public async Task No_republica_tras_descarga_ni_necesita_original_para_reconocer_entrega()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        File.Delete(root.Xml("salida/42/ticket.xml"));
        File.Delete(source);
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.False(File.Exists(root.Xml("salida/42/ticket.xml")));
    }

    [Fact]
    public async Task Hash_incorrecto_no_publica_y_se_recupera_al_reparar_origen()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml", "correcto");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        File.WriteAllText(source, "incompleto");
        await Assert.ThrowsAsync<IOException>(() => new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default));
        Assert.False(Directory.Exists(root.Xml("salida/42")));
        File.WriteAllText(source, "correcto");
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.Equal("correcto", File.ReadAllText(root.Xml("salida/42/ticket.xml")));
    }

    [Fact]
    public async Task Registro_corrupto_no_republica_ni_sobrescribe()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        Directory.CreateDirectory(root.Xml("salida/42"));
        File.WriteAllText(root.Xml("salida/42/manifest.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default));
        Assert.False(File.Exists(root.Xml("salida/42/ticket.xml")));
    }

    [Fact]
    public async Task Mismo_nombre_distintas_recepciones_no_se_sobrescriben()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("ticket.xml", "primero");
        var publisher = new TicketDeliveryPublisher();
        await publisher.PublishAsync(root.Xml("salida"), new(1, source, Sha256FileHasher.ComputeHex(source)), default);
        File.WriteAllText(source, "segundo");
        await publisher.PublishAsync(root.Xml("salida"), new(2, source, Sha256FileHasher.ComputeHex(source)), default);
        Assert.Equal("primero", File.ReadAllText(root.Xml("salida/1/ticket.xml")));
        Assert.Equal("segundo", File.ReadAllText(root.Xml("salida/2/ticket.xml")));
    }
}
