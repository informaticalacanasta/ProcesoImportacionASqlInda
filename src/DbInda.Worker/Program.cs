using DbInda.Worker.Configuration;
using DbInda.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbIndaOptions(builder.Configuration);
builder.Services.AddHostedService<TicketImportWorker>();

var host = builder.Build();
host.Run();
