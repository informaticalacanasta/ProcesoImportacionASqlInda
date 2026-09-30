using DbInda.Worker.Files;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class TicketDeliveryWorker(TicketDeliveryLookup lookup, TicketDeliveryPublisher publisher,
    IOptions<TicketDeliveryOptions> options, ILogger<TicketDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Directory.CreateDirectory(config.Directory);
                using var ownership = new FileStream(Path.Combine(config.Directory, ".publisher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                long after = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    var batch = (await lookup.ReadAsync(config.MinimumReceptionId, after, stoppingToken)).ToArray();
                    if (batch.Length == 0) break;
                    foreach (var item in batch)
                    {
                        try { await publisher.PublishAsync(config.Directory, item, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { logger.LogError(ex, "Entrega de ticket {Id} pendiente; se reintentará.", item.Id); }
                        after = item.Id;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Bandeja de tickets pendiente; se reintentará."); }
            try { await Task.Delay(TimeSpan.FromSeconds(config.ScanIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
