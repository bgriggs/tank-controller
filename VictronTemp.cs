using BigMission.VictronSdk.Modbus;
using Microsoft.Extensions.Logging;

namespace TankController;

public interface ITemperature
{
    public Task<double?> GetTemperatureF(string ip, int port, byte sensorId, ILogger logger);
}

internal class VictronTemp : ITemperature
{
    public async Task<double?> GetTemperatureF(string ip, int port, byte sensorId, ILogger logger)
    {
        return await TemperatureSource.GetTemperatureF(ip, port, sensorId, logger);
    }
}
