using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class PedidoImportWorker : BackgroundService
{
    private readonly PedidoProcessor _processor;
    private readonly OrderOptions _options;
    private readonly ILogger<PedidoImportWorker> _logger;
    private readonly InFlightPathTracker _inFlight = new();

    public PedidoImportWorker(PedidoProcessor processor, IOptions<OrderOptions> options, ILogger<PedidoImportWorker> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Order import worker started. PendingPath={Path}", _options.Pending);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Importación de pedidos pendiente; se reintentará sin detener las ventas.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.Pending);
        Directory.CreateDirectory(_options.Processed);
        Directory.CreateDirectory(_options.Errors);
        if (!Directory.Exists(_options.Pending))
            return;

        var files = Directory.EnumerateFiles(_options.Pending)
            .Where(PedidoFileClassifier.IsImportCandidate)
            .ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
            CancellationToken = cancellationToken
        }, async (file, token) =>
        {
            var normalized = FilePathNormalizer.Normalize(file);
            if (!_inFlight.TryClaim(normalized))
                return;
            try
            {
                await _processor.ProcessAsync(normalized, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo importar el pedido. File={File}", Path.GetFileName(file));
            }
            finally
            {
                _inFlight.Release(normalized);
            }
        }).ConfigureAwait(false);
    }
}
