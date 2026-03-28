using System.Device.Gpio;

namespace TankController;

public interface IRelayControl
{
    int GpioPin { get; }
    bool IsOn { get; }

    void InitializePin(int gpioPin);

    void TurnOn();
    void TurnOff();
}

internal class RpiRelay : IRelayControl
{
    public bool IsOn { get; private set; }
    public int GpioPin { get; private set; }
    private readonly GpioController controller = new();

    public void InitializePin(int gpioPin)
    {
        if (controller.IsPinOpen(GpioPin))
            controller.ClosePin(GpioPin);

        GpioPin = gpioPin;
        controller.OpenPin(GpioPin, PinMode.Output);
    }

    public void TurnOff()
    {
        controller.Write(GpioPin, PinValue.High);
        IsOn = false;
    }

    public void TurnOn()
    {
        controller.Write(GpioPin, PinValue.Low);
        IsOn = true;
    }
}