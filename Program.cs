using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace TankController;

internal class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddNLog();
        });

        // Add servces and background service
        builder.Services.AddSingleton<IRelayControl, RpiRelay>();
        builder.Services.AddKeyedSingleton<ILedControl, RpiLed>("SystemLed");
        builder.Services.AddKeyedSingleton<ILedControl, RpiLed>("HeatLed");
        builder.Services.AddSingleton<ITemperature, VictronTemp>();
        builder.Services.AddHostedService<Service>();

        using IHost host = builder.Build();
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(Program).GetType().Name);

        logger.LogInformation("Starting application");
        await host.RunAsync();
    }
}
