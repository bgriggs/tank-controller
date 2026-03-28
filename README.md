# Tank Controller
Monitors outside temperature and enables tank heaters

## Hardware
- Raspberry Pi 4
- 2 x LED Metal Indicator Light (https://www.amazon.com/dp/B09L7W7HKL?ref_=ppx_hzsearch_conn_dt_b_fed_asin_title_1)
- Relay Module (https://www.amazon.com/dp/B0B1ZHXXXD?ref_=ppx_hzsearch_conn_dt_b_fed_asin_title_2)

## Operation
The controller connects with Victron Cerbo generic sensors to monitor the outside temperature. If the temperature drops below a certain threshold, the controller activates the relay to turn on the tank heaters. The LED indicators show the status of the system: one for power and one for heater activation.

## Connections

### Relay Module
| Raspberry Pi         | Relay Module |
|----------------------|--------------|
| GPIO 26 (Pin 37)    | IN (Signal)  |
| 5V (Pin 2)          | VCC          |
| GND (Pin 39)        | GND          |

> The relay is active low: writing `Low` to the GPIO pin turns the relay **on**, writing `High` turns it **off**.

### Power LED (System Running)
| Raspberry Pi         | LED            |
|----------------------|----------------|
| GPIO 16 (Pin 36)    | Anode (+)      |
| GND (Pin 34)        | Cathode (−)    |

### Heater LED (Heater Active)
| Raspberry Pi         | LED            |
|----------------------|----------------|
| GPIO 13 (Pin 33)    | Anode (+)      |
| GND (Pin 30)        | Cathode (−)    |

## Example Project
See https://github.com/bgriggs/pi-temp-relay/tree/main/PiTempControlledRelay for an example connecting to Victron for the temperature and controlling a relay.