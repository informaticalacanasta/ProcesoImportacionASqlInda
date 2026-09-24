using Microsoft.Extensions.Configuration;
using DbInda.Worker.Configuration;
using DbInda.Worker.Logging;
using DbInda.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbIndaOptions(builder.Configuration);

var logsDirectory = builder.Configuration["Paths:Logs"];
if (!string.IsNullOrWhiteSpace(logsDirectory))
{
    var retainedDays = builder.Configuration.GetValue("Logging:RetainedDays", 31);
    var maxFileBytes = builder.Configuration.GetValue("Logging:MaxFileBytes", 20L * 1024 * 1024);
    builder.Logging.AddProvider(new DailyFileLoggerProvider(logsDirectory, retainedDays, maxFileBytes));
}
builder.Services.AddHostedService<TicketImportWorker>();
builder.Services.AddHostedService<FileOrganizationWorker>();

var host = builder.Build();
host.Run();
