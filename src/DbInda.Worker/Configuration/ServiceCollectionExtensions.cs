using DbInda.Worker.Configuration;
using DbInda.Worker.Inbound;
using DbInda.Worker.Parsing;
using DbInda.Worker.Persistence;
using DbInda.Worker.Processing;
using DbInda.Worker.Validation;
using DbInda.Worker.Files;
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

        return services;
    }
}
