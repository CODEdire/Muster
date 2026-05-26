using Muster.Infrastructure;
using Muster.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMusterInfrastructure();

builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
