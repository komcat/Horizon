# Power supply — UNI-T UDP3305S-E (and Keysight E36xxx)

How a programmable power supply reaches every layer of Horizon, with the UNI-T
UDP3305S-E on the LAN as the worked example.

## Hardware

| Model | Channels / ranges | Transport | `type` |
|---|---|---|---|
| UNI-T **UDP3305S-E** | CH1, CH2: 0–32 V / 0–5 A · CH3: 0–6 V / 0–3 A | LAN (raw SCPI socket) **or** NI-VISA (USBTMC / VXI-11) | `unit_t_udp3305s` |
| Keysight E36441A / E36103B | 4 / 1 | LXI — SCPI on TCP :5025 | `keysight_e36441a_direct`, `keysight_e36103b_direct` |

## Config — `%APPDATA%\Horizon\config\power_supply_devices.json`

```jsonc
{
  "devices": [
    {
      "name": "UDP3305S",          // the device name blocks / scripts / recipes use
      "type": "unit_t_udp3305s",
      "ip": "192.168.0.88",        // LAN: raw SCPI socket at ip:port
      "port": 5025,
      "channels": 3
      // or instead of ip/port (wins when both are set):
      // "visaResource": "TCPIP0::192.168.0.88::INSTR"             (VXI-11 via NI-VISA)
      // "visaResource": "USB0::0x0483::0x5740::<serial>::INSTR"   (USBTMC; UNI-T uses the STMicro VID)
    }
  ]
}
```

> **Bench check:** SCPI port `5025` is the assumed default — confirm it on the
> supply's front panel (Utility › LAN). If the socket doesn't answer, switch to the
> VXI-11 `visaResource` form above. A `Horizon.ConfigTool` preset carries the same entry.

## Layer 0 — `Horizon.PowerSupply`

- `Transport/ScpiTransport.cs` — `IScpiTransport` with two implementations:
  `TcpScpiTransport` (no VISA runtime; connect / send / receive all time-bounded,
  responses capped at 4 KB) and `VisaScpiTransport` (USBTMC or VXI-11).
- `UnitTUDP3305SAdapter` picks the transport from config (`visaResource` → VISA, else
  `ip` → TCP). Setpoints outside a channel's rated range are refused **before anything
  is sent** (`PowerSupplyError.OutOfRange`). `Disconnect` forces every output OFF first.
- `PowerSupplyManager` dispatches on `type` (unknown types fall back to Keysight).

## Layer 1 — `IPowerSupplyFacet`

Unchanged shape: connect / disconnect, `SetVoltageAsync`, `SetCurrentLimitAsync`,
`OutputOnAsync` / `OutputOffAsync`, `RefreshAsync` + cached `GetState`, 2 s background
poll. `OutOfRange` maps to an `InvalidParameter` result with a readable message.

## Layer 2 — the same operations on three surfaces

| Operation | Program block (`.hwf`, `StepDispatcher`) | `.moon` script (`machine.psu.*`) | `.hrcp` recipe (`psu.<dev>.*`) |
|---|---|---|---|
| Connect / disconnect | `psu.connect` / `psu.disconnect` | `connect(dev)` / `disconnect(dev)` | `connect()` / `disconnect()` |
| Set V / I-limit | `psu.setVoltage` / `psu.setCurrentLimit` | `setVoltage(dev[,ch],V)` / `setCurrentLimit(dev[,ch],A)` | `setVoltage(channel:, volts:)` / `setCurrentLimit(channel:, amps:)` |
| Output | `psu.outputOn` / `psu.outputOff` | `outputOn(dev[,ch])` / `outputOff(dev[,ch])` | `outputOn(channel:)` / `outputOff(channel:)` |
| Read | `psu.readVoltage` / `psu.readCurrent` (`captureAs`) | `readVoltage(dev[,ch])` / `readCurrent(dev[,ch])` | `readVoltage(channel:, store:)` / `readCurrent(...)` |
| **Verified power-up** | **`psu.powerUp`** | **`powerUp(dev, ch, V, A [, opts])` → `{v, i}`** | **`powerUp(channel:, volts:, amps:, …)`** |

`psu.disconnect` always switches every output OFF before closing the link.

### `psu.powerUp` — verified power-up

One shared implementation, `Horizon.Workflow/Execution/PsuPowerUp.cs`
(`PsuPowerUp.RunAsync`), behind all three surfaces so they can't drift:

1. **Verify the link** — if disconnected, reconnect once; fail if that fails.
2. **Current limit, then voltage** — the limit first, so the load is protected the
   instant the output turns on. Out-of-range values fail here, nothing sent.
3. **Output ON.**
4. **Settle** `settleMs` (default **150 ms**, max 10 s), then **poll the read-back**
   every 100 ms for up to `VerifyMs` (default **2 s**, max 10 s) until it passes. A single
   early read is not enough: the UDP3305S was seen reading `0.000 V / 0.300 A` 150 ms after
   ON on an output that came up fine. Passes only if the output reads ON and
   `|V − setpoint| ≤ toleranceV` (default 0.1 V). A sag with the current at ≥ 98 % of the
   limit is reported as **constant-current mode** (overload / short) — unless the request
   sets `AcceptCurrentLimit` (the FAAM Run-page panel does), which returns success with a
   CC-mode warning, matching a by-hand ON of a load that runs at its current limit.

**Fail-safe:** once the output was commanded ON, any failure — or a cancel during the
settle / verify — switches it back **OFF** (unless `offOnFail` is false). Verified readings are
stored on success only: `{captureAs}_v` / `_i` (block), `{store}_v` / `_i` (recipe),
the returned `{ v, i }` table (script).

```lua
local r = machine.psu.powerUp("UDP3305S", 1, 5.0, 0.5, { settleMs = 150, toleranceV = 0.1 })
machine.log(("CH1 %.3f V %.4f A"):format(r.v, r.i))
```

### `IProcessService.IsRunning`

True while a program, deck or recipe run is in progress (paused or not). Added so
Layer 3 can lock manual controls during a run; the same condition that refuses a
product switch mid-run.

## Layer 3 — FAAM.UI

- **Engineer (Gantt editor)** — a **Power** palette category (`DeviceKind.PowerSupply`)
  with all nine `psu.*` blocks plus help entries; they drop onto the **PSU** lanes.
- **Machine › Power supply** — per-channel V / I setpoints, output toggle, live readings;
  shows the adapter type, and each channel's command result (incl. range refusals) on
  the status line.
- **Run page — `Views/PsuQuickPanel`** (replaced the old hand-operated Dispense panel;
  the dispense routines / editor / `dispense.*` blocks are unaffected):
  - one row per shown channel: live **V** and **A** in a green-on-black device readout
    (Consolas bold 28 pt, same look as the Process timer) and an **ON/OFF** toggle that
    shows the polled output state (green = ON);
  - **CH1 is always shown**; `+ CH2` / `+ CH3` add rows and `×` removes them — a channel
    whose output is ON can't be removed. The choice is saved per supply in
    `%APPDATA%\Horizon\config\faam_psu_panel.json` (`{ "UDP3305S": [1, 3] }`);
  - **ON is a verified power-up** (`PsuPowerUp`, 10 s bound) at the channel's Run-page
    setpoints, edited on **Machine › Settings › Run page power supply** and saved per supply /
    channel in `faam_psu_defaults.json` (`{ "UDP3305S": { "1": { "volts": 3.3, "amps": 1 } } }`);
    a channel with no entry uses **CV 3.3 V / 1.0 A** (constant voltage; the current limit is the per-channel variable). OFF is a plain output-off;
  - readings refresh every 500 ms off the UI thread, only while the panel is visible;
  - the **toggles lock during a run** — a Run-page step / alignment / scan
    (`ActivityGate`) or a Layer 2 program / deck / recipe (`IsRunning`) — re-checked at
    click time. Readouts keep updating while locked. The Machine-page section is not
    locked, like the other Machine-page manual controls.
