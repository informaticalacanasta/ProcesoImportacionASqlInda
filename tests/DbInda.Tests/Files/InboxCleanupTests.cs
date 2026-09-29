using System.Text.Json;
using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Files;

public sealed class InboxCleanupTests : IDisposable
{
    private readonly TempFolder _root = new();
    private readonly ManualClock _clock = new();

    [Fact]
    public async Task Un_txt_publicado_espera_desde_EligibleSinceUtc_y_despues_se_borra()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "80052_dato_tpv.TXT", "datos");
        var entry = Completed(path, hash, "Txt");
        entry.CopyDestination = Path.Combine(_root.Path, "inboxOrganizado", "dato_tpv.TXT");
        WriteEntry(inbox, entry, retired: true);
        var logger = new ListLogger();
        var cleanup = Create(inbox, logger);

        await cleanup.ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        var mark = ReadMark(inbox, entry.Id);
        Assert.Equal(hash, mark.Sha256, ignoreCase: true);
        Assert.Equal(_clock.Utc, mark.EligibleSinceUtc);
        Assert.False(File.Exists(entry.CopyDestination));

        _clock.Utc = _clock.Utc.AddHours(23);
        await cleanup.ScanAsync(CancellationToken.None);
        Assert.True(File.Exists(path));

        _clock.Utc = _clock.Utc.AddHours(2);
        await cleanup.ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Contains(logger.Lines, line => line.Contains("Inbox cleanup deleted completed file. File=80052_dato_tpv.TXT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Una_llegada_repetida_con_el_mismo_nombre_y_hash_no_se_borra()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "80052_CLIENTES.TXT", "mismo");
        var previous = Completed(path, hash, "Txt");
        WriteEntry(inbox, previous, retired: true);
        Seed(inbox, previous.Id, hash, _clock.Utc.AddHours(-48));
        var current = Completed(path, hash, "Txt");
        current.State = "Staged";
        WriteEntry(inbox, current, retired: false);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(LedgerPath(inbox, previous.Id)));
    }

    [Fact]
    public async Task Dos_registros_historicos_con_el_mismo_hash_no_autorizan_el_borrado()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "bnd50167", "lote");
        var first = Completed(path, hash, "Marker");
        var second = Completed(path, hash, "Marker");
        WriteEntry(inbox, first, retired: true);
        WriteEntry(inbox, second, retired: true);
        Seed(inbox, first.Id, hash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(LedgerPath(inbox, first.Id)));
    }

    [Fact]
    public async Task Si_el_organizador_tiene_el_bloqueo_la_limpieza_se_aplaza()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "80052_dato_tpv.TXT", "ocupado");
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        using var held = new FileStream(Path.Combine(inbox, ".organizador.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(inbox, ".limpieza")));
    }

    [Fact]
    public async Task Un_espejo_solo_en_memoria_no_autoriza_borrar_el_pedido()
    {
        var inbox = Inbox();
        var mirror = MirrorDir();
        var name = "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT";
        var (path, hash) = WriteInbox(inbox, name, "pedido");
        WriteEntry(inbox, Completed(path, hash, "Txt"), retired: true);
        var journal = new PedidoMirrorJournal(mirror, NullLogger<PedidoMirrorJournal>.Instance);
        var record = new PedidoMirrorRecord { Sha256 = hash, State = PedidoMirrorStates.Prepared, FinalName = name };
        journal.Save(record);
        record.State = PedidoMirrorStates.Mirrored;

        Assert.Equal(PedidoMirrorStates.Mirrored, journal.Find(hash)!.State);
        Assert.False(journal.IsPersistentlyMirrored(hash));
        Assert.Contains("PREPARED", File.ReadAllText(Path.Combine(mirror, hash + ".json")), StringComparison.Ordinal);

        var cleanup = Create(inbox, journal);
        _clock.Utc = _clock.Utc.AddHours(48);
        await cleanup.ScanAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(inbox, ".limpieza")));

        journal.Save(record);
        await cleanup.ScanAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        _clock.Utc = _clock.Utc.AddHours(25);
        await cleanup.ScanAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(mirror, hash + ".json")));
    }

    [Fact]
    public async Task Un_pedido_recien_espejado_no_hereda_la_fecha_del_historial()
    {
        var inbox = Inbox();
        var name = "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT";
        var (path, hash) = WriteInbox(inbox, name, "recien");
        var entry = Completed(path, hash, "Txt");
        var historial = WriteEntry(inbox, entry, retired: true);
        File.SetLastWriteTimeUtc(historial, _clock.Utc.UtcDateTime.AddDays(-10));
        Mirror(inbox, hash, name);

        var cleanup = Create(inbox);
        await cleanup.ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_clock.Utc, ReadMark(inbox, entry.Id).EligibleSinceUtc);

        _clock.Utc = _clock.Utc.AddHours(25);
        await cleanup.ScanAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Un_historial_corrupto_no_autoriza_el_borrado()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "80052_dato_tpv.TXT", "corrupto");
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-48));
        var garbageId = Guid.NewGuid().ToString("N");
        var garbage = Path.Combine(inbox, ".organizacion", "historial", garbageId[..2], garbageId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(garbage)!);
        File.WriteAllText(garbage, "{");

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task La_cancelacion_detiene_la_limpieza_antes_de_borrar()
    {
        var inbox = Inbox();
        var first = Ready(inbox, "80052_uno.TXT", "uno");
        var second = Ready(inbox, "80052_dos.TXT", "dos");
        using var cts = new CancellationTokenSource();
        _clock.OnUtc = () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(inbox).ScanAsync(cts.Token));

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Tras_reiniciar_la_elegibilidad_persistida_sigue_contando()
    {
        var inbox = Inbox();
        var name = "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT";
        var (path, hash) = WriteInbox(inbox, name, "reinicio");
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        Mirror(inbox, hash, name);

        await Create(inbox).ScanAsync(CancellationToken.None);
        Assert.True(File.Exists(path));

        _clock.Utc = _clock.Utc.AddHours(25);
        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(MirrorDir(), hash + ".json")));
    }

    [Fact]
    public async Task Con_sql_caido_la_limpieza_solo_usa_archivos()
    {
        var inbox = Inbox();
        var (path, hash) = WriteInbox(inbox, "80052_dato_tpv.TXT", "sin-sql");
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-25));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Un_bnd_se_borra_cuando_sus_miembros_estan_publicados_aunque_sigan_en_inbox()
    {
        var inbox = Inbox();
        var (memberPath, memberHash) = WriteInbox(inbox, "50167_dato_tpv.TXT", "miembro");
        var member = Completed(memberPath, memberHash, "Txt");
        WriteEntry(inbox, member, retired: true);
        var (markerPath, markerHash) = WriteInbox(inbox, "bnd50167", "");
        var marker = Completed(markerPath, markerHash, "Marker", members: [member.Id]);
        WriteEntry(inbox, marker, retired: true);
        Seed(inbox, marker.Id, markerHash, _clock.Utc.AddHours(-25));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(markerPath));
        Assert.True(File.Exists(memberPath));
    }

    [Fact]
    public async Task Un_bnd_no_se_borra_si_un_miembro_sigue_en_el_diario_activo()
    {
        var inbox = Inbox();
        var member = Completed(Path.Combine(inbox, "50167_dato_tpv.TXT"), new string('A', 64), "Txt");
        member.State = "Staged";
        WriteEntry(inbox, member, retired: false);
        var (markerPath, markerHash) = WriteInbox(inbox, "bnd50167", "indicador");
        var marker = Completed(markerPath, markerHash, "Marker", members: [member.Id]);
        WriteEntry(inbox, marker, retired: true);
        Seed(inbox, marker.Id, markerHash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(markerPath));
        Assert.False(File.Exists(LedgerPath(inbox, marker.Id)));
    }

    [Fact]
    public async Task Collision_y_un_archivo_sin_historial_se_conservan()
    {
        var inbox = Inbox();
        var (collisionPath, hash) = WriteInbox(inbox, "80052_CLIENTES.TXT", "choque");
        var collision = Completed(collisionPath, hash, "Txt");
        collision.State = "Collision";
        WriteEntry(inbox, collision, retired: false);
        var orphan = WriteInbox(inbox, "80052_otro.TXT", "huerfano").Path;

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(collisionPath));
        Assert.True(File.Exists(orphan));
        Assert.False(Directory.Exists(Path.Combine(inbox, ".limpieza")));
    }

    [Fact]
    public async Task No_borra_subcarpetas_ni_enlaces_simbolicos()
    {
        var inbox = Inbox();
        var nestedDir = Path.Combine(inbox, "anidado");
        Directory.CreateDirectory(nestedDir);
        var (nested, hash) = WriteInbox(nestedDir, "80052_dato_tpv.TXT", "anidado");
        var entry = Completed(nested, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-48));

        var outside = Path.Combine(_root.Path, "fuera.txt");
        File.WriteAllText(outside, "enlace");
        var link = Path.Combine(inbox, "80052_enlace.TXT");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            link = "";
        }

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(nested));
        if (link.Length > 0)
            Assert.True(File.Exists(link));
    }

    [Fact]
    public async Task Un_PEDIDOS_TMPP_no_reconocido_no_se_borra_aunque_la_elegibilidad_tenga_48_horas()
    {
        var inbox = Inbox();
        var (path, _, entry) = Publish(inbox, "50167_PED_0260924120000_00167_1_PEDIDOS_TMPP.TXT", "caja-corta");
        Seed(inbox, entry.Id, entry.Hash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(LedgerPath(inbox, entry.Id)));
    }

    [Fact]
    public async Task Un_PEDIDOS_TMPP_no_reconocido_se_conserva_aunque_el_hash_este_en_el_espejo()
    {
        var inbox = Inbox();
        var name = "50167_PED_0260924120000_00167_1_PEDIDOS_TMPP.TXT";
        var (path, hash, entry) = Publish(inbox, name, "caja-corta-con-espejo");
        Mirror(inbox, hash, name);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(LedgerPath(inbox, entry.Id)));
    }

    [Fact]
    public async Task Un_pedido_valido_en_PREPARED_se_conserva()
    {
        var inbox = Inbox();
        var name = "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT";
        var (path, hash, entry) = Publish(inbox, name, "prepared");
        var journal = new PedidoMirrorJournal(MirrorDir(), NullLogger<PedidoMirrorJournal>.Instance);
        journal.Save(new PedidoMirrorRecord { Sha256 = hash, State = PedidoMirrorStates.Prepared, FinalName = name });
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-48));

        await Create(inbox, journal).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(PedidoMirrorStates.Prepared, JsonSerializer.Deserialize<PedidoMirrorRecord>(File.ReadAllText(Path.Combine(MirrorDir(), hash + ".json")))!.State);
    }

    [Fact]
    public async Task Un_pedido_valido_sin_espejo_se_conserva()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT", "sin-espejo");
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(LedgerPath(inbox, entry.Id)));
    }

    [Fact]
    public async Task Un_pedido_valido_MIRRORED_se_borra_cuando_la_retencion_se_cumple()
    {
        var inbox = Inbox();
        var name = "50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP.TXT";
        var (path, hash, entry) = Publish(inbox, name, "listo");
        Mirror(inbox, hash, name);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-25));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(MirrorDir(), hash + ".json")));
    }

    [Fact]
    public async Task Las_minusculas_y_REPETIDO_no_eluden_la_comprobacion_de_pedido()
    {
        var inbox = Inbox();
        var id = "0123456789abcdef0123456789abcdef";
        var validLower = "50167_ped_0260924120000_00167_00001_pedidos_tmpp.txt";
        var validRepeated = $"50167_PED_0260924120000_00167_00001_PEDIDOS_TMPP_REPETIDO_20260924_100000_{id}.TXT";
        var ambiguousLower = "50167_ped_0260924120000_00167_1_pedidos_tmpp.txt";
        var ambiguousRepeated = $"50167_PED_0260924120000_00167_1_PEDIDOS_TMPP_REPETIDO_20260924_100000_{id}.TXT";

        var kept = Publish(inbox, validLower, "minusculas");
        Seed(inbox, kept.Entry.Id, kept.Hash, _clock.Utc.AddHours(-48));
        var repeated = Publish(inbox, validRepeated, "repetido");
        Seed(inbox, repeated.Entry.Id, repeated.Hash, _clock.Utc.AddHours(-48));
        var ambiguous = Publish(inbox, ambiguousLower, "minusculas-ambiguas");
        Mirror(inbox, ambiguous.Hash, ambiguousLower);
        Seed(inbox, ambiguous.Entry.Id, ambiguous.Hash, _clock.Utc.AddHours(-48));
        var ambiguousRep = Publish(inbox, ambiguousRepeated, "repetido-ambiguo");
        Mirror(inbox, ambiguousRep.Hash, ambiguousRepeated);
        Seed(inbox, ambiguousRep.Entry.Id, ambiguousRep.Hash, _clock.Utc.AddHours(-48));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(kept.Path), validLower);
        Assert.True(File.Exists(repeated.Path), validRepeated);
        Assert.True(File.Exists(ambiguous.Path), ambiguousLower);
        Assert.True(File.Exists(ambiguousRep.Path), ambiguousRepeated);
    }

    [Fact]
    public async Task Un_txt_generico_dato_tpv_se_borra_sin_espejo()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "generico");
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-25));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Una_marca_sin_EligibleSinceUtc_no_borra_y_abre_una_espera_nueva()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "sin-fecha");
        var ledger = WriteRawMark(inbox, entry.Id, $$"""
        {
          "EntryId": "{{entry.Id}}",
          "Sha256": "{{hash}}"
        }
        """);
        File.SetLastWriteTimeUtc(ledger, _clock.Utc.UtcDateTime.AddDays(-10));
        File.SetLastWriteTimeUtc(path, _clock.Utc.UtcDateTime.AddDays(-10));

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_clock.Utc, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_con_EligibleSinceUtc_nulo_no_borra_y_abre_una_espera_nueva()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "fecha-nula");
        WriteRawMark(inbox, entry.Id, $$"""
        {
          "EntryId": "{{entry.Id}}",
          "Sha256": "{{hash}}",
          "EligibleSinceUtc": null
        }
        """);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_clock.Utc, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_con_fecha_mal_formada_no_borra()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "fecha-mala");
        WriteRawMark(inbox, entry.Id, $$"""
        {
          "EntryId": "{{entry.Id}}",
          "Sha256": "{{hash}}",
          "EligibleSinceUtc": "ayer"
        }
        """);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_clock.Utc, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_con_fecha_minima_no_borra()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "fecha-minima");
        WriteRawMark(inbox, entry.Id, $$"""
        {
          "EntryId": "{{entry.Id}}",
          "Sha256": "{{hash}}",
          "EligibleSinceUtc": "0001-01-01T00:00:00+00:00"
        }
        """);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_clock.Utc, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_con_fecha_futura_no_borra()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "fecha-futura");
        var future = _clock.Utc.AddDays(2);
        Seed(inbox, entry.Id, hash, future);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(future, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_valida_no_reinicia_la_retencion()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "marca-valida");
        var since = _clock.Utc.AddHours(-10);
        Seed(inbox, entry.Id, hash, since);

        await Create(inbox).ScanAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(since, ReadMark(inbox, entry.Id).EligibleSinceUtc);
    }

    [Fact]
    public async Task Una_marca_regenerada_no_borra_antes_de_24_horas_y_sobrevive_al_reinicio()
    {
        var inbox = Inbox();
        var (path, hash, entry) = Publish(inbox, "80052_dato_tpv.TXT", "regenerada");
        WriteRawMark(inbox, entry.Id, $$"""
        {
          "EntryId": "{{entry.Id}}",
          "Sha256": "{{hash}}"
        }
        """);
        var started = _clock.Utc;

        await Create(inbox).ScanAsync(CancellationToken.None);
        Assert.Equal(started, ReadMark(inbox, entry.Id).EligibleSinceUtc);

        _clock.Utc = started.AddHours(23);
        await Create(inbox).ScanAsync(CancellationToken.None);
        Assert.True(File.Exists(path));
        Assert.Equal(started, ReadMark(inbox, entry.Id).EligibleSinceUtc);

        _clock.Utc = started.AddHours(25);
        await Create(inbox).ScanAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    public void Dispose() => _root.Dispose();

    private string Inbox()
    {
        var path = Path.Combine(_root.Path, "inbox");
        Directory.CreateDirectory(path);
        return path;
    }

    private string MirrorDir()
    {
        var path = Path.Combine(_root.Path, "mirror");
        Directory.CreateDirectory(path);
        return path;
    }

    private InboxCleanup Create(string inbox, ILogger<InboxCleanup>? logger = null)
        => Create(inbox, new PedidoMirrorJournal(MirrorDir(), NullLogger<PedidoMirrorJournal>.Instance), logger);

    private InboxCleanup Create(string inbox, PedidoMirrorJournal journal, ILogger<InboxCleanup>? logger = null)
        => new(
            Options.Create(new PathsOptions { Input = _root.Path }),
            Options.Create(new OrganizationOptions { Inbox = inbox }),
            Options.Create(new InboxCleanupOptions { RetentionHours = 24, ScanIntervalMinutes = 10 }),
            journal,
            _clock,
            logger ?? NullLogger<InboxCleanup>.Instance);

    private void Mirror(string inbox, string hash, string finalName)
    {
        _ = inbox;
        new PedidoMirrorJournal(MirrorDir(), NullLogger<PedidoMirrorJournal>.Instance).Save(new PedidoMirrorRecord
        {
            Sha256 = hash,
            State = PedidoMirrorStates.Mirrored,
            FinalName = finalName
        });
    }

    private string Ready(string inbox, string name, string contents)
    {
        var (path, hash) = WriteInbox(inbox, name, contents);
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        Seed(inbox, entry.Id, hash, _clock.Utc.AddHours(-25));
        return path;
    }

    private (string Path, string Hash, OrganizationEntry Entry) Publish(string inbox, string name, string contents)
    {
        var (path, hash) = WriteInbox(inbox, name, contents);
        var entry = Completed(path, hash, "Txt");
        WriteEntry(inbox, entry, retired: true);
        return (path, hash, entry);
    }

    private static string WriteRawMark(string inbox, string entryId, string json)
    {
        var path = LedgerPath(inbox, entryId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private static (string Path, string Hash) WriteInbox(string directory, string name, string contents)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return (path, Sha256FileHasher.ComputeHex(path));
    }

    private static OrganizationEntry Completed(string path, string hash, string kind, string? id = null, IEnumerable<string>? members = null)
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Kind = kind,
            Destination = path,
            Hash = hash,
            State = "Complete",
            Members = members?.ToList() ?? []
        };

    private static string WriteEntry(string inbox, OrganizationEntry entry, bool retired)
    {
        var path = retired
            ? Path.Combine(inbox, ".organizacion", "historial", entry.Id[..2], entry.Id + ".json")
            : Path.Combine(inbox, ".organizacion", entry.Id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entry));
        return path;
    }

    private static void Seed(string inbox, string entryId, string hash, DateTimeOffset since)
        => new InboxCleanupLedger(Path.Combine(inbox, ".limpieza")).Remember(entryId, hash, since);

    private static string LedgerPath(string inbox, string entryId)
        => Path.Combine(inbox, ".limpieza", entryId + ".json");

    private static InboxCleanupMark ReadMark(string inbox, string entryId)
        => JsonSerializer.Deserialize<InboxCleanupMark>(File.ReadAllText(LedgerPath(inbox, entryId)))!;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        public Action? OnUtc { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            OnUtc?.Invoke();
            return Utc;
        }
    }

    private sealed class ListLogger : ILogger<InboxCleanup>
    {
        public List<string> Lines { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
