using System.Collections.Concurrent;
using System.Threading.Channels;
using DbInda.Worker.Configuration;
using DbInda.Worker.Processing;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Inbound;

public sealed class InboundXmlPipeline
{
    private readonly Channel<string> _channel;
    private readonly InFlightPathTracker _tracker = new();
    private readonly FileReadinessChecker _readiness;
    private readonly IInboundFileProcessor _processor;
    private readonly SqlRetryScheduler _retries;
    private readonly ILogger<InboundXmlPipeline> _logger;
    private readonly int _consumerCount;
    private readonly ConcurrentDictionary<Task, byte> _discoveries = new();
    private Task[] _consumers = [];
    private int _started;

    public InboundXmlPipeline(
        IOptions<ProcessingOptions> processing,
        FileReadinessChecker readiness,
        IInboundFileProcessor processor,
        SqlRetryScheduler retries,
        ILogger<InboundXmlPipeline> logger)
    {
        var options = processing.Value;
        _readiness = readiness;
        _processor = processor;
        _retries = retries;
        _logger = logger;
        _consumerCount = options.MaxConcurrency;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int InFlightCount => _tracker.Count;

    public int QueuedCount => _channel.Reader.Count;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _consumers = new Task[_consumerCount];
        for (var i = 0; i < _consumerCount; i++)
            _consumers[i] = ConsumeAsync();
    }

    public void Submit(string path, CancellationToken cancellationToken)
    {
        var task = DiscoverAsync(path, cancellationToken);
        _discoveries.TryAdd(task, 0);
        _ = task.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _discoveries,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task ShutdownAsync()
    {
        var discoveries = _discoveries.Keys.ToArray();
        if (discoveries.Length > 0)
            await Task.WhenAll(discoveries).ConfigureAwait(false);

        _channel.Writer.TryComplete();

        if (_consumers.Length > 0)
            await Task.WhenAll(_consumers).ConfigureAwait(false);
    }

    private async Task DiscoverAsync(string path, CancellationToken cancellationToken)
    {
        string? normalized = null;
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            normalized = FilePathNormalizer.Normalize(path);
            if (!_tracker.TryClaim(normalized))
            {
                _logger.LogInformation("Ruta XML duplicada ignorada: {Path}", normalized);
                return;
            }

            _logger.LogInformation("Archivo XML descubierto: {Path}", normalized);

            var ready = await _readiness.WaitUntilReadyAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                _logger.LogInformation("Archivo XML no encolado porque no está estable o ya no está disponible: {Path}", normalized);
                _tracker.Release(normalized);
                return;
            }

            if (_retries.ShouldDefer(normalized, out var nextAttemptUtc))
            {
                _logger.LogInformation(
                    "Próximo retry SQL de {Path} a las {NextAttemptUtc}. No se encola todavía.",
                    normalized,
                    nextAttemptUtc);
                _tracker.Release(normalized);
                return;
            }

            _logger.LogInformation("Archivo XML encolado: {Path}", normalized);
            await _channel.Writer.WriteAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (normalized is not null)
                _tracker.Release(normalized);
        }
        catch (ChannelClosedException)
        {
            if (normalized is not null)
                _tracker.Release(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción aislada al preparar XML: {Path}", normalized ?? path);
            if (normalized is not null)
                _tracker.Release(normalized);
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var path in _channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                await ProcessOneAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "El consumidor de XML terminó de forma inesperada.");
        }
    }

    private async Task ProcessOneAsync(string path)
    {
        _logger.LogInformation("Inicio de procesamiento XML: {Path}", path);
        try
        {
            await _processor.ProcessAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción aislada procesando XML: {Path}", path);
        }
        finally
        {
            _tracker.Release(path);
            _logger.LogInformation("Fin de procesamiento XML: {Path}", path);
        }
    }
}
