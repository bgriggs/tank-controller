using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using TankController;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddNLog();

builder.Services.AddSingleton<StatusTracker>(sp =>
    new StatusTracker(
        dataFilePath: Path.Combine(AppContext.BaseDirectory, "data", "relay-events.json"),
        logger: sp.GetRequiredService<ILogger<StatusTracker>>()));
builder.Services.AddSingleton<IRelayControl, RpiRelay>();
builder.Services.AddKeyedSingleton<ILedControl, RpiLed>("SystemLed");
builder.Services.AddKeyedSingleton<ILedControl, RpiLed>("HeatLed");
builder.Services.AddSingleton<ITemperature, VictronTemp>();
builder.Services.AddHostedService<Service>();

// Listen on port 80 (AmbientCapabilities=CAP_NET_BIND_SERVICE set in the systemd unit)
builder.WebHost.UseUrls("http://*:80");

var app = builder.Build();

app.UseDefaultFiles();   // maps / → /index.html
app.UseStaticFiles();    // serves wwwroot/

app.MapGet("/api/status", (StatusTracker tracker) => Results.Ok(tracker.GetSnapshot()));

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TankController");
logger.LogInformation("Starting application");
await app.RunAsync();
