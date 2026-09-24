using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Processing;
using DbInda.Worker.Validation;
using DbInda.Worker.Files;
using DbInda.Worker.Alerts;
using DbInda.Worker.Tracking;
using DbInda.Worker.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DbInda.Worker.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDbIndaOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<PathsOptions>, PathsOptionsValidator>();
        services.AddSingleton<IValidateOptions<ProcessingOptions>, ProcessingOptionsValidator>();
        services.AddSingleton<IValidateOptions<RetryOptions>, RetryOptionsValidator>();
        services.AddSingleton<IValidateOptions<SqlOptions>, SqlOptionsValidator>();
        services.AddSingleton<IValidateOptions<XsdValidationOptions>, XsdValidationOptionsValidator>();
        services.AddSingleton<IValidateOptions<TrackingOptions>, TrackingOptionsValidator>();

        services.AddOptions<PathsOptions>()
            .Bind(configuration.GetSection(PathsOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ProcessingOptions>()
            .Bind(configuration.GetSection(ProcessingOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<RetryOptions>()
            .Bind(configuration.GetSection(RetryOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<SqlOptions>()
            .Bind(configuration.GetSection(SqlOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<XsdValidationOptions>()
            .Bind(configuration.GetSection(XsdValidationOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<LoggingOptions>()
            .Bind(configuration.GetSection(LoggingOptions.SectionName));
        services.AddOptions<TrackingOptions>()
            .Bind(configuration.GetSection(TrackingOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<OrganizationOptions>()
            .Bind(configuration.GetSection("Organization"))
            .Validate(o => o.MaxConcurrency > 0 && o.ScanIntervalSeconds > 0 && o.ReadinessTimeoutSeconds > 0,
                "Organization: la concurrencia y los intervalos deben ser positivos.")
            .ValidateOnStart();
        services.AddSingleton<IArchivedInvoiceLookup, ArchivedInvoiceLookup>();
        services.AddSingleton<ReceivedFileOrganizer>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<ReceptionRepository>();
        services.AddSingleton<TicketRepository>();
        services.AddSingleton<TicketDocumentReader>();
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IOptions<PathsOptions>>().Value;
            var xsd = sp.GetRequiredService<IOptions<XsdValidationOptions>>().Value;
            return new TicketXsdValidator(paths.Xsd, xsd.SchemaFileName);
        });
        services.AddSingleton<TicketImportProcessor>();
        services.AddSingleton<SqlRetryScheduler>();
        services.AddSingleton<IXmlFileArchiver, XmlFileArchiver>();
        services.AddSingleton<XmlArchiveReconciler>();
        services.AddSingleton<IInboundFileProcessor, TicketInboundFileProcessor>();
        services.AddSingleton<IFileStabilityProbe, FileSystemStabilityProbe>();
        services.AddSingleton<FileReadinessChecker>();
        services.AddSingleton<InboundXmlPipeline>();
        services.AddSingleton<InputDirectoryScanner>();
        services.AddSingleton<InputXmlWatcher>();
        services.AddSingleton<InboundActivity>();
        services.AddSingleton<IScanActivity>(sp => sp.GetRequiredService<InboundActivity>());
        services.AddSingleton(sp =>
        {
            var directory = TrackingDirectory(sp);
            var tracking = sp.GetRequiredService<IOptions<TrackingOptions>>().Value;
            return new TrackingOutbox(
                directory,
                tracking.OutboxMaxFiles,
                tracking.OutboxMaxBytes,
                sp.GetRequiredService<ILogger<TrackingOutbox>>());
        });
        services.AddSingleton(sp => new FileObservationRegistry(
            Path.Combine(TrackingDirectory(sp), "observaciones.json"),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IFileArrivalClock>(sp => sp.GetRequiredService<FileObservationRegistry>());
        services.AddSingleton<ITrackingStore, SqlTrackingStore>();
        services.AddSingleton<ImportTracker>();
        services.AddSingleton(sp => new AlertStateStore(
            Path.Combine(TrackingDirectory(sp), "alertas-estado.json"),
            sp.GetRequiredService<ILogger<AlertStateStore>>()));
        services.AddSingleton<AlertEngine>();
        services.AddSingleton(sp => new LocalAlertSink(
            Path.Combine(TrackingDirectory(sp), "alertas.log"),
            sp.GetRequiredService<ILogger<LocalAlertSink>>()));
        services.AddSingleton<WebhookAlertSink>();
        services.AddHostedService<TrackingLifecycleService>();
        services.AddHostedService<ImportHealthWorker>();

        return services;
    }

    private static string TrackingDirectory(IServiceProvider services)
    {
        var tracking = services.GetRequiredService<IOptions<TrackingOptions>>().Value;
        var paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        return TrackingPaths.ResolveOutbox(tracking, paths);
    }
}
