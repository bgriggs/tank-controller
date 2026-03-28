using System.Device.Gpio;

namespace TankController;

public interface ILedControl
{
    int GpioPin { get; }
    void InitializePin(int gpioPin);
    void TurnOn();
    void TurnOff();
}

internal class RpiLed : ILedControl
{
    public int GpioPin { get; private set; }
    private readonly GpioController controller = new();

    public void InitializePin(int gpioPin)
    {
        if (controller.IsPinOpen(GpioPin))
            controller.ClosePin(GpioPin);

        GpioPin = gpioPin;
        controller.OpenPin(GpioPin, PinMode.Output);
        controller.Write(GpioPin, PinValue.Low);
    }

    public void TurnOff()
    {
        controller.Write(GpioPin, PinValue.Low);
    }

    public void TurnOn()
    {
        controller.Write(GpioPin, PinValue.High);
    }
}
