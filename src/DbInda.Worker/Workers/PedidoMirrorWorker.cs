using DbInda.Worker.Configuration;
using DbInda.Worker.Orders;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class PedidoMirrorWorker(
    PedidoFileMirror mirror,
    IOptions<OrderOptions> options,
    ILogger<PedidoMirrorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = options.Value.Pending;
        logger.LogInformation("Order mirror started. PendingPath={Pending}", pending);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await mirror.ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Espejo de pedidos pendiente; se reintentará sin detener la importación.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.Value.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
