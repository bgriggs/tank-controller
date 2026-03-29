using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TankController;

internal class Service : BackgroundService
{
    private readonly IConfiguration configuration;
    private readonly IRelayControl relayControl;
    private readonly ITemperature temperatureSource;
    private readonly ILedControl systemLed;
    private readonly ILedControl heatLed;
    private readonly StatusTracker statusTracker;

    private ILogger Logger { get; }

    public Service(IConfiguration configuration, ILoggerFactory loggerFactory, IRelayControl relayControl, ITemperature temperatureSource,
        StatusTracker statusTracker,
        [FromKeyedServices("SystemLed")] ILedControl systemLed,
        [FromKeyedServices("HeatLed")] ILedControl heatLed)
    {
        this.configuration = configuration;
        this.relayControl = relayControl;
        this.temperatureSource = temperatureSource;
        this.statusTracker = statusTracker;
        this.systemLed = systemLed;
        this.heatLed = heatLed;
        Logger = loggerFactory.CreateLogger(GetType().Name);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var restartCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            var changeToken = ((IConfigurationRoot)configuration).GetReloadToken();
            using var changeReg = changeToken.RegisterChangeCallback(_ =>
            {
                Logger.LogInformation("Configuration changed, restarting service");
                restartCts.Cancel();
            }, null);

            await RunAsync(restartCts.Token);

            if (!stoppingToken.IsCancellationRequested)
            {
                Logger.LogInformation("Restarting with updated configuration");
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var intervalMs = configuration.GetValue<int>("TempCheckIntervalMs");
        Logger.LogInformation($"Starting service on interval: {intervalMs}ms");

        var ip = configuration.GetValue<string>("CerboIP") ?? "192.168.1.100";
        Logger.LogInformation($"Using IP: {ip}");
        var tempThresholdF = configuration.GetValue<double>("TempThresholdDegF");
        Logger.LogInformation($"Using temp threshold: {tempThresholdF}F");
        var tempThresholdOffDefF = configuration.GetValue<double>("TempThresholdOffDegF");
        Logger.LogInformation($"Using temp threshold off: {tempThresholdOffDefF}F");
        var sensorId = configuration.GetValue<byte>("SensorVRMInstance");
        Logger.LogInformation($"Using VRM sensor ID: {sensorId}");
        var relayControlPin = configuration.GetValue<int>("RelayGPIOPin");
        relayControl.InitializePin(relayControlPin);
        Logger.LogInformation($"Using relay control GPIO pin: {relayControlPin}");

        var heatOnLed = configuration.GetValue<int>("HeatOnLed");
        Logger.LogInformation($"Heat ON LED: {heatOnLed}");
        var systemRunningLed = configuration.GetValue<int>("SystemRunningLed");
        Logger.LogInformation($"System running LED: {systemRunningLed}");
        systemLed.InitializePin(systemRunningLed);
        heatLed.InitializePin(heatOnLed);

        // Initial state is off
        relayControl.TurnOff();
        heatLed.TurnOff();

        using var watchdog = new Timer(_ =>
        {
            Logger.LogWarning("Watchdog triggered: loop has not updated in {Interval}ms", intervalMs * 2);
            systemLed.TurnOff();
        }, null, Timeout.Infinite, Timeout.Infinite);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var temperature = await temperatureSource.GetTemperatureF(ip, 502, sensorId, Logger);

                if (temperature.HasValue)
                    statusTracker.UpdateTemperature(temperature.Value);

                // Turn on relay if temperature is below threshold
                if (temperature <= tempThresholdF)
                {
                    relayControl.TurnOn();
                    heatLed.TurnOn();
                    statusTracker.RecordRelayOn(temperature!.Value);
                    Logger.LogInformation($"Relay turned on: {temperature} < {tempThresholdF}");
                }

                // Turn off relay if temperature is above threshold
                if (temperature > tempThresholdOffDefF)
                {
                    relayControl.TurnOff();
                    heatLed.TurnOff();
                    statusTracker.RecordRelayOff(temperature!.Value);
                    Logger.LogInformation($"Relay turned off: {temperature} <= {tempThresholdOffDefF}");
                }

                systemLed.TurnOn();
                watchdog.Change(intervalMs * 2, Timeout.Infinite);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in main loop");
                systemLed.TurnOff();
            }
            finally
            {
                try
                {
                    await Task.Delay(intervalMs, token);
                }
                catch (OperationCanceledException) { }
            }
        }

        watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        relayControl.TurnOff();
        heatLed.TurnOff();
        systemLed.TurnOff();
    }
}
