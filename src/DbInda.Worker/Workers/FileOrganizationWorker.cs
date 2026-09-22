using DbInda.Worker.Configuration;
using DbInda.Worker.Files;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class FileOrganizationWorker(ReceivedFileOrganizer organizer, IOptions<OrganizationOptions> options,
    ILogger<FileOrganizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await organizer.ScanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Organizador pendiente; se reintentará sin detener la importación XML."); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.ScanIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}