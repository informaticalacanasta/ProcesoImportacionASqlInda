using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class InboxCleanupWorker(
    InboxCleanup cleanup,
    IOptions<InboxCleanupOptions> options,
    ILogger<InboxCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await cleanup.ScanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Limpieza de inbox pendiente; se reintentará sin detener la importación."); }
            try { await Task.Delay(TimeSpan.FromMinutes(options.Value.ScanIntervalMinutes), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
