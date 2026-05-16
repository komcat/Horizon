# Bench Test — Lighting / TEC / SMU / Power Supply Facets

> **Handoff note.** Four new Layer 1 facets were added on a development
> machine *without* these instruments. They compile and are committed but are
> **not bench-tested**. This file is the test plan for a session on a system
> that has the real hardware. Pick this up from a fresh session there.

## What to test

Four engine facets, added across commits `cbb67ce` (Power Supply),
`d1a63fc` (Lighting), `8da10b8` (TEC), `20b09fa` (SMU), with Layer 2 reach in
`9273aec`. Each connects, polls, and drives one instrument family:

| Facet | Hardware | Transport | Driver confidence |
|---|---|---|---|
| `IPowerSupplyFacet` | Keysight E36xxx | LXI — SCPI over TCP :5025 | SCPI standard-ish, **untested** |
| `ILightingFacet` | CCS PD3 LED controller | TCP socket | **placeholder wire format** |
| `ITecFacet` | Thorlabs TED4015 | VISA (USB) | SCPI plausible, **untested** |
| `ISmuFacet` | Keithley 2400 SourceMeter | VISA (GPIB) | SCPI from bobcat ref — trusted |

## Driver wire-format — confirm before trusting a pass

Each adapter's command strings are isolated to one place — fix there if wrong:

- **PSU** — `KeysightPowerSupplyAdapter` (`Command` / `GetStatus`): `INST:NSEL`,
  `VOLT`, `CURR`, `OUTP`, `MEAS:VOLT?`/`MEAS:CURR?`. Standard SCPI; verify the
  exact E36441A/E36103B model accepts them.
- **Lighting** — `CcsPd3Adapter.SendIntensity`: currently `L{ch},{intensity}\r\n`
  — a **guess**. Replace with the real PD3 channel-intensity command.
- **TEC** — `Ted4015Adapter` (`SetTemperature`/`TecOn`/`TecOff`/`GetStatus`):
  `source:temperature:spoint`, `output:state`, `measure:temperature?` — confirm
  against the TED4015 programming manual.
- **SMU** — `Keithley2400Adapter`: SCPI taken from the bobcat-engine C++ driver
  (`bobcat_engine/include/SMU/`) — should be correct as-is.

## Setup — config files

The engine reads `%APPDATA%\Horizon\config\`. Author these with
`Horizon.ConfigTool` (Lighting / TEC / SMU / Power supply domains → Add → edit
fields → Save) or hand-write them:

- `lighting_config.json` — `{ "lightingControllers": [ { deviceId, ipAddress, port, controllerType:"CCS_PD3", channels:[…] } ] }`
- `tec_devices.json` — `{ "tecDevices": [ { name, controllerType:"TED4015_Direct", visaResource } ] }`
- `smu_devices.json` — `{ "devices": [ { name, type:"keithley_2400", visaResource } ] }`
- `power_supply_config.json` — `{ "devices": [ { name, type, ip, port:5025, channels } ] }`

**Engine filters:** `tec_devices.json` is read for `TED4015`-type rows only
(CLD101x stays in the Laser facet); `smu_devices.json` for `keithley_2400`-type
rows only (the 6482 stays in the Measurement facet).

## Test procedure

Use `Horizon.Workflow.Console` (Layer 1 + 2 host).

**1. Connect check.** Launch the console — it runs `ConnectAllAsync`. The
startup log must show, per domain:

```
Power supply ConnectAll — 1/1 device(s) connected.
Lighting ConnectAll — 1/1 controller(s) connected.
TEC ConnectAll — 1/1 device(s) connected.
SMU ConnectAll — 1/1 device(s) connected.
```

`0/1` → connection failed (IP/VISA resource wrong, or device offline).

**2. Drive each device** with a recipe. Load a product, write a `.hrec`:

```
# psu — set ch1 to 3.3 V, 0.5 A limit, output on
psu.PSU1.setCurrentLimit(channel: 1, amps: 0.5)
psu.PSU1.setVoltage(channel: 1, volts: 3.3)
psu.PSU1.outputOn(channel: 1)

# lighting — channel 0 to 50%
lighting.led-controller-1.setIntensity(channel: 0, intensity: 128)

# tec — 25 C, TEC on
tec.tec-main.setTemperature(celsius: 25.0)
tec.tec-main.tecOn()

# smu — source 1 V, output on, then read
smu.Keithley_2400.setCompliance(value: 0.001)
smu.Keithley_2400.setSourceVoltage(volts: 1.0)
smu.Keithley_2400.outputOn()
```

Run it; each call should return OK and the instrument should physically respond
(measure with a meter / observe the lamp / read the TEC display).

**3. Status poll.** After a `set`, the 2 s poll loop should reflect the new
state — visible in the status snapshot / device tables.

## Pass criteria

Per device: connects (`1/1`), a facet call physically changes the hardware, and
the polled state reflects it. A driver that connects but whose commands do
nothing → fix that adapter's command method (see the wire-format section).

## When done

Record results here (per device: pass / fixed-then-pass / blocked), commit any
adapter command-string fixes, and update the "Driver confidence" table above.
