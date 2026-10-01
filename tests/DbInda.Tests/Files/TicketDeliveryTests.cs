using DbInda.Tests.Inbound;
using DbInda.Worker.Files;

namespace DbInda.Tests.Files;

public sealed class TicketDeliveryTests
{
    [Fact]
    public async Task Publica_xml_y_dos_pdf_verificados_y_conserva_originales()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root);
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        foreach (var name in new[] { "fact_sin_firmar.xml", "fact_sin_firmar.pdf", "fact_a4_sin_firmar.pdf" })
            Assert.Equal(File.ReadAllBytes(root.Xml(name)), File.ReadAllBytes(root.Xml("salida/42/" + name)));
        Assert.True(File.Exists(root.Xml("salida/42/manifest.json")));
        Assert.False(Directory.Exists(root.Xml("salida/.staging/42")));
    }

    [Fact]
    public async Task Espera_los_dos_pdf_y_publica_el_conjunto_al_llegar_el_segundo()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("fact_sin_firmar.xml");
        root.WriteXml("fact_sin_firmar.pdf", "normal");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default));
        Assert.False(Directory.Exists(root.Xml("salida/42")));
        root.WriteXml("fact_a4_sin_firmar.pdf", "a4");
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.Equal(3, Directory.GetFiles(root.Xml("salida/42")).Count(path => !path.EndsWith("manifest.json")));
    }

    [Fact]
    public async Task No_republica_tras_descarga_ni_necesita_original_para_reconocer_entrega()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root);
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        File.Delete(root.Xml("salida/42/fact_sin_firmar.xml"));
        File.Delete(source);
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.False(File.Exists(root.Xml("salida/42/fact_sin_firmar.xml")));
    }

    [Fact]
    public async Task Hash_incorrecto_no_publica_y_se_recupera_al_reparar_origen()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root, "correcto");
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        File.WriteAllText(source, "incompleto");
        await Assert.ThrowsAsync<IOException>(() => new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default));
        Assert.False(Directory.Exists(root.Xml("salida/42")));
        File.WriteAllText(source, "correcto");
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.Equal("correcto", File.ReadAllText(root.Xml("salida/42/fact_sin_firmar.xml")));
    }

    [Fact]
    public async Task Registro_corrupto_no_republica_ni_sobrescribe()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root);
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        Directory.CreateDirectory(root.Xml("salida/42"));
        File.WriteAllText(root.Xml("salida/42/manifest.json"), "{}");
        await Assert.ThrowsAsync<IOException>(() => new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default));
        Assert.False(File.Exists(root.Xml("salida/42/fact_sin_firmar.xml")));
    }

    [Fact]
    public async Task Mismo_nombre_distintas_recepciones_no_se_sobrescriben()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root, "primero");
        var publisher = new TicketDeliveryPublisher();
        await publisher.PublishAsync(root.Xml("salida"), new(1, source, Sha256FileHasher.ComputeHex(source)), default);
        File.WriteAllText(source, "segundo");
        File.WriteAllText(root.Xml("fact_sin_firmar.pdf"), "pdf-segundo");
        File.WriteAllText(root.Xml("fact_a4_sin_firmar.pdf"), "a4-segundo");
        await publisher.PublishAsync(root.Xml("salida"), new(2, source, Sha256FileHasher.ComputeHex(source)), default);
        Assert.Equal("primero", File.ReadAllText(root.Xml("salida/1/fact_sin_firmar.xml")));
        Assert.Equal("segundo", File.ReadAllText(root.Xml("salida/2/fact_sin_firmar.xml")));
        Assert.Equal("pdf-segundo", File.ReadAllText(root.Xml("salida/2/fact_sin_firmar.pdf")));
    }

    [Fact]
    public async Task Completa_entrega_antigua_que_solo_tenia_xml()
    {
        using var root = new TempFolder();
        var source = WriteTicket(root);
        var item = new TicketDeliveryItem(42, source, Sha256FileHasher.ComputeHex(source));
        Directory.CreateDirectory(root.Xml("salida/42"));
        File.Copy(source, root.Xml("salida/42/fact_sin_firmar.xml"));
        File.WriteAllText(root.Xml("salida/42/manifest.json"), System.Text.Json.JsonSerializer.Serialize(item));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.True(File.Exists(root.Xml("salida/42/fact_sin_firmar.pdf")));
        Assert.True(File.Exists(root.Xml("salida/42/fact_a4_sin_firmar.pdf")));
    }

    [Fact]
    public async Task Reconoce_nombres_de_pdf_cuando_el_xml_archivado_tiene_sufijo_de_colision()
    {
        using var root = new TempFolder();
        var source = root.WriteXml("fact_sin_firmar_R17.xml");
        root.WriteXml("fact_sin_firmar_R17.pdf", "normal");
        root.WriteXml("fact_a4_sin_firmar_R17.pdf", "a4");
        var item = new TicketDeliveryItem(17, source, Sha256FileHasher.ComputeHex(source));
        await new TicketDeliveryPublisher().PublishAsync(root.Xml("salida"), item, default);
        Assert.Equal("normal", File.ReadAllText(root.Xml("salida/17/fact_sin_firmar_R17.pdf")));
        Assert.Equal("a4", File.ReadAllText(root.Xml("salida/17/fact_a4_sin_firmar_R17.pdf")));
    }

    private static string WriteTicket(TempFolder root, string xml = "<TicketBai />")
    {
        var source = root.WriteXml("fact_sin_firmar.xml", xml);
        root.WriteXml("fact_sin_firmar.pdf", "pdf-normal");
        root.WriteXml("fact_a4_sin_firmar.pdf", "pdf-a4");
        return source;
    }
}
