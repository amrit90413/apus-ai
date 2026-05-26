using Analytics.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((_, lc) => lc.WriteTo.Console());
builder.Services.AddHostedService<UsageConsumer>();
builder.Build().Run();
