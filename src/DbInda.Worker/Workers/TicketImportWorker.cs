using System.Data.Common;
using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Processing;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Workers;

public sealed class TicketImportWorker : BackgroundService
{
    private readonly ILogger<TicketImportWorker> _logger;
    private readonly PathsOptions _paths;
    private readonly ProcessingOptions _processing;
    private readonly SqlOptions _sql;
    private readonly InboundXmlPipeline _pipeline;
    private readonly InputDirectoryScanner _scanner;
    private readonly InputXmlWatcher _watcher;
    private readonly XmlArchiveReconciler _reconciler;

    public TicketImportWorker(
        ILogger<TicketImportWorker> logger,
        IOptions<PathsOptions> paths,
        IOptions<ProcessingOptions> processing,
        IOptions<SqlOptions> sql,
        InboundXmlPipeline pipeline,
        InputDirectoryScanner scanner,
        InputXmlWatcher watcher,
        XmlArchiveReconciler reconciler)
    {
        _logger = logger;
        _paths = paths.Value;
        _processing = processing.Value;
        _sql = sql.Value;
        _pipeline = pipeline;
        _scanner = scanner;
        _watcher = watcher;
        _reconciler = reconciler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DbInda Ticket Importer iniciado (FASE 3B). Carpeta entrada: {Input}. Máxima concurrencia: {MaxConcurrency}. Cola: {QueueCapacity}. XSD: {Xsd}. SQL Server: {SqlTarget}.",
            _paths.Input,
            _processing.MaxConcurrency,
            _processing.QueueCapacity,
            _paths.Xsd,
            DescribeSqlTarget(_sql.DbInda));

        try
        {
            await _reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reconciliación inicial de archivo fallida. Se continúa el arranque.");
        }

        _pipeline.Start();
        _watcher.TryStart(_paths.Input, path => _pipeline.Submit(path, stoppingToken));

        var scannerTask = _scanner.RunAsync(
            _paths.Input,
            TimeSpan.FromSeconds(_processing.ScanIntervalSeconds),
            path => _pipeline.Submit(path, stoppingToken),
            stoppingToken,
            async ct =>
            {
                try
                {
                    await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Reconciliación de archivo en ciclo de scan fallida.");
                }
            });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("Parada del worker: se deja de aceptar trabajo nuevo.");
        _watcher.Stop();

        try
        {
            await scannerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _pipeline.ShutdownAsync().ConfigureAwait(false);
        _logger.LogInformation("DbInda Ticket Importer detenido.");
    }

    private static string DescribeSqlTarget(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            builder.Remove("Password");
            builder.Remove("Pwd");
            var server = builder.ContainsKey("Server") ? builder["Server"] : builder.ContainsKey("Data Source") ? builder["Data Source"] : "?";
            var database = builder.ContainsKey("Database") ? builder["Database"] : builder.ContainsKey("Initial Catalog") ? builder["Initial Catalog"] : "?";
            return $"{server}/{database}";
        }
        catch
        {
            return "(connection string no interpretable)";
        }
    }
}
