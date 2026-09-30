using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

// The TPVs always write to Paths:Input. Import begins only after an XML is
// stable and has been atomically moved to the ticket pending directory.
public sealed class TicketStagingWorker(
    IOptions<PathsOptions> paths,
    IOptions<ProcessingOptions> processing,
    FileReadinessChecker readiness,
    ILogger<TicketStagingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StageOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "No se pudieron preparar los XML pendientes."); }
            try { await Task.Delay(TimeSpan.FromSeconds(processing.Value.ScanIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task StageOnceAsync(CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(paths.Value.Input);
        var pending = Path.GetFullPath(paths.Value.TicketPending);
        Directory.CreateDirectory(pending);
        if (!Directory.Exists(input)) return;
        var files = Directory.EnumerateFiles(input, "*", SearchOption.TopDirectoryOnly)
            .Where(FilePathNormalizer.HasXmlExtension).ToArray();
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = processing.Value.MaxConcurrency, CancellationToken = cancellationToken },
            async (source, token) =>
            {
                var destination = Path.Combine(pending, Path.GetFileName(source));
                if (File.Exists(destination)) return;
                var observed = await readiness.WaitForStableObservationAsync(source, token);
                if (observed is null || !readiness.Matches(source, observed.Value)) return;
                try
                {
                    File.Move(source, destination, overwrite: false);
                    logger.LogInformation("XML recibido: {Source} -> {Destination}", source, destination);
                }
                catch (IOException ex) when (File.Exists(destination) || !File.Exists(source))
                {
                    logger.LogDebug(ex, "Traslado XML aplazado: {Source}", source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "El XML permanece en recepción y se reintentará: {Source}", source);
                }
            });
    }
}
