using DbInda.Tests.Inbound;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbInda.Tests.Orders;

public sealed class PedidoProcessorTests
{
    [Fact]
    public async Task Un_pedido_valido_se_archiva_por_fecha_y_tienda()
    {
        using var root = new TempFolder();
        var (processor, repo, pending) = Create(root);
        var file = Write(pending);
        await processor.ProcessAsync(file, default);

        Assert.Equal(1, repo.Saves);
        Assert.False(File.Exists(file));
        var archived = Path.Combine(root.Path, "procesados", "tienda_167", "tpv_001", "2026", "09", "23", PedidoSamples.Name);
        Assert.True(File.Exists(archived));
    }

    [Fact]
    public async Task Un_hash_ya_procesado_solo_mueve_el_fichero()
    {
        using var root = new TempFolder();
        var (processor, repo, pending) = Create(root);
        repo.Existing = new PedidoReceptionRow { IdRecepcion = 4, Estado = PedidoReceptionStatuses.Procesado, IdPedido = 9 };
        var file = Write(pending);
        await processor.ProcessAsync(file, default);

        Assert.Equal(0, repo.Saves);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(root.Path, "procesados", "tienda_167", "tpv_001", "2026", "09", "23", PedidoSamples.Name)));
    }

    [Fact]
    public async Task Un_fallo_sql_transitorio_deja_el_fichero_en_pendientes()
    {
        using var root = new TempFolder();
        var (processor, repo, pending) = Create(root);
        repo.Next = PedidoSaveResult.Transient("timeout");
        var file = Write(pending);
        await processor.ProcessAsync(file, default);
        await processor.ProcessAsync(file, default);

        Assert.True(File.Exists(file));
        Assert.Equal(1, repo.Saves);
    }

    [Fact]
    public async Task Un_txt_invalido_va_a_errores_sin_insertar_el_pedido()
    {
        using var root = new TempFolder();
        var (processor, repo, pending) = Create(root);
        var file = Write(pending, PedidoSamples.HeaderLine());
        await processor.ProcessAsync(file, default);

        Assert.Equal(0, repo.Saves);
        Assert.Equal(1, repo.Errors);
        Assert.False(File.Exists(file));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root.Path, "errores"), PedidoSamples.Name, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Si_el_commit_ya_quedo_y_falla_el_traslado_el_siguiente_ciclo_solo_mueve()
    {
        using var root = new TempFolder();
        var (processor, repo, pending) = Create(root);
        var file = Write(pending);
        string? blocked = null;
        repo.OnSave = request =>
        {
            blocked = request.FinalPath;
            Directory.CreateDirectory(request.FinalPath);
            return PedidoSaveResult.Saved(10, 20);
        };

        await processor.ProcessAsync(file, default);
        Assert.True(File.Exists(file));
        Assert.Equal(1, repo.Saves);

        Directory.Delete(blocked!, recursive: true);
        repo.Existing = new PedidoReceptionRow { IdRecepcion = 20, Estado = PedidoReceptionStatuses.Procesado, IdPedido = 10 };
        await processor.ProcessAsync(file, default);

        Assert.Equal(1, repo.Saves);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(root.Path, "procesados", "tienda_167", "tpv_001", "2026", "09", "23", PedidoSamples.Name)));
    }

    [Theory]
    [InlineData(2627, "Cannot insert duplicate key. DS_HASH_SHA256", true)]
    [InlineData(2601, "unique index UX_PEDIDO DS_HASH_SHA256", true)]
    [InlineData(2627, "UX_TICKET_HASH_SHA256", false)]
    public void El_duplicado_de_pedido_no_se_confunde_con_el_de_ventas(int number, string message, bool duplicate)
        => Assert.Equal(duplicate, PedidoSqlErrors.IsHashDuplicate(number, message));

    [Theory]
    [InlineData(-2, (byte)11, true)]
    [InlineData(1205, (byte)13, true)]
    [InlineData(53, (byte)20, true)]
    [InlineData(2627, (byte)14, false)]
    [InlineData(245, (byte)16, false)]
    public void Solo_los_fallos_de_conexion_o_bloqueo_son_transitorios(int number, byte severity, bool transient)
        => Assert.Equal(transient, PedidoSqlErrors.IsTransient(number, severity));

    private static string Write(string pending, string? contents = null)
    {
        Directory.CreateDirectory(pending);
        var path = Path.Combine(pending, PedidoSamples.Name);
        File.WriteAllText(path, contents ?? PedidoSamples.File());
        return path;
    }

    private static (PedidoProcessor Processor, FakeOrders Repository, string Pending) Create(TempFolder root)
    {
        var orders = Options.Create(new OrderOptions
        {
            Pending = Path.Combine(root.Path, "pendientes"),
            Processed = Path.Combine(root.Path, "procesados"),
            Errors = Path.Combine(root.Path, "errores"),
            MaxConcurrency = 2,
            ScanIntervalSeconds = 10
        });
        var processing = Options.Create(new ProcessingOptions
        {
            StableChecks = 1,
            StableCheckDelayMilliseconds = 1,
            MaxConcurrency = 1,
            QueueCapacity = 1,
            ScanIntervalSeconds = 1
        });
        var checker = new FileReadinessChecker(
            processing,
            TimeProvider.System,
            new FileSystemStabilityProbe(),
            NullLogger<FileReadinessChecker>.Instance);
        var repo = new FakeOrders();
        var processor = new PedidoProcessor(
            orders,
            Options.Create(new RetryOptions()),
            checker,
            new PedidoTxtParser(),
            repo,
            TimeProvider.System,
            NullLogger<PedidoProcessor>.Instance);
        return (processor, repo, orders.Value.Pending);
    }

    private sealed class FakeOrders : IPedidoRepository
    {
        public PedidoReceptionRow? Existing { get; set; }
        public PedidoSaveResult Next { get; set; } = PedidoSaveResult.Saved(10, 20);
        public Func<PedidoSaveRequest, PedidoSaveResult>? OnSave { get; set; }
        public int Saves { get; private set; }
        public int Errors { get; private set; }

        public Task<PedidoReceptionRow?> FindByHashAsync(string hash, CancellationToken cancellationToken)
            => Task.FromResult(Existing);

        public Task<PedidoSaveResult> SaveAsync(PedidoSaveRequest request, CancellationToken cancellationToken)
        {
            Saves++;
            return Task.FromResult(OnSave?.Invoke(request) ?? Next);
        }

        public Task<PedidoErrorWrite> TryInsertErrorAsync(
            string fileName, string sourcePath, string hash, long size, int? storeFromName, int? cashFromName,
            DateTime? fileDate, string error, CancellationToken cancellationToken)
        {
            Errors++;
            return Task.FromResult(PedidoErrorWrite.Recorded);
        }

        public Task TryUpdateFinalPathAsync(long receptionId, string finalPath, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
