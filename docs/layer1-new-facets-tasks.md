# Layer 1 — New Facets: Lighting, TEC, SMU, Power Supply

> Task file. The ConfigTool work (`Horizon.Config`, Phase 1.5) modeled the
> *configuration* for four device types the field uses — but they are
> config-only: no driver, no manager, and `IHorizonEngine` exposes no facet
> for them. A workflow or recipe cannot drive a lamp, a standalone TEC, a
> SourceMeter, or a power supply. This task adds Layer 1 support.

## Context

`Horizon.Config` (call it "Layer 0" — pure config records + JSON loaders) gave
us `LightingControllerConfig`, `TecDeviceConfig`, `SmuDeviceConfig`,
`PowerSupplyConfig`. Layer 1 (`Horizon.Engine`) now needs **facets** so these
devices become first-class hardware the engine connects, polls, and drives —
and so Layer 2 recipes/workflows can call them (`lighting.*`, `psu.*`, …).

## The facet pattern (recap)

Each engine domain is a facet interface + implementation in
`Horizon.Engine/Facets/`, wrapping a domain project (a manager + hardware
adapters), exposing **engine-owned record types only**, with a poll loop and a
`StateChanged` event. Example: `ILaserFacet` → `LaserFacet` →
`Horizon.LaserController` (`LaserControllerManager` + `CLD101xAdapter`).

A new facet therefore needs, per device type:

1. **Domain project** — hardware adapter(s) + a manager.
2. **Hardware adapter** — the real driver (TCP socket / VISA / SCPI).
3. **Engine-owned models** — `…DeviceInfo`, `…State` records (no driver types leak).
4. **Facet** — `I…Facet` interface + `…Facet` implementation.
5. **`IHorizonEngine`** — a new facet property.
6. **`HorizonEngine` wiring** — load config via the `Horizon.Config` loader,
   create the manager, connect in `ConnectAllAsync`, start the poll loop.

## The four device types

| Device | Hardware | Transport | New driver? |
|---|---|---|---|
| Lighting | CCS_PD3 LED controller | TCP socket | yes — fully new |
| TEC | Thorlabs TED4015 (TEC-only) | VISA (USB) | yes — and overlaps CLD101x |
| SMU | Keithley 2400 SourceMeter | VISA (GPIB) | yes — and overlaps Keithley 6482 |
| Power supply | Keysight E36441A / E36103B | LXI — SCPI over TCP | yes — fully new |

VISA drivers can reuse the existing `Native/VisaNative.cs` P/Invoke pattern
(Keithley 6482, CLD101x already use it). TCP/LXI drivers are plain sockets.

## Decisions to make first

Two device types overlap existing facets — resolve before building:

- **TEC vs Laser.** `Horizon.LaserController` already drives the CLD101x
  (laser + TEC). The field's `tec_devices.json` unifies CLD101x *and* the
  TEC-only TED4015. Options: (a) a new `ITecFacet` covering all TEC-capable
  controllers, with `laser_controller_devices.json` superseded by
  `tec_devices.json`; or (b) extend `ILaserFacet` with TED4015 support.
  Leaning (a) — it matches the field config.

- **SMU vs Measurement.** `IMeasurementFacet` drives the Keithley 6482
  (measure-only). The 2400 also *sources* (voltage/current with compliance).
  Options: (a) a new `ISmuFacet` for sourcing instruments; or (b) widen the
  Measurement facet/domain into a general SMU domain covering both.
  Leaning (b) — one current-measurement/SMU domain, 6482 + 2400.

- **Config-model home.** Config records currently live in `Horizon.Config`.
  The new domain projects can reference `Horizon.Config` for them (keeps one
  config-model home) rather than each owning a copy as the legacy domains do.

## Task breakdown

| Task | Deliverable |
|---|---|
| 0 | Resolve the TEC/Laser and SMU/Measurement decisions above. |
| 1 | **Power supply** — `Horizon.PowerSupply` (Keysight LXI SCPI adapter + manager), `IPowerSupplyFacet`, engine wiring. Cleanest: no overlap, simple SCPI. |
| 2 | **Lighting** — `Horizon.Lighting` (CCS_PD3 TCP adapter + manager), `ILightingFacet` (per-channel intensity), engine wiring. |
| 3 | **TEC** — per the Task 0 decision: `ITecFacet` + TED4015 VISA adapter (and CLD101x folded in, or kept in Laser). |
| 4 | **SMU** — per the Task 0 decision: Keithley 2400 VISA adapter; widen Measurement or add `ISmuFacet`. |
| 5 | **Layer 2 reach** — once a facet exists, extend `RecipeMethodRegistry` and the workflow `StepDispatcher` so recipes/programs can call it (`lighting.<dev>.setIntensity`, `psu.<dev>.setVoltage`, …). |

Start with Power supply (Task 1) — no overlap, plain SCPI — as the template;
the other three follow the same six-step facet pattern.

## Facet sketches (to refine during build)

```
ILightingFacet     SetChannelIntensity(device, channel, percent), GetState …
IPowerSupplyFacet  SetVoltage / SetCurrentLimit / OutputOn/Off (per channel)
ITecFacet          SetTemperature / TecOn / TecOff, GetState
ISmuFacet (or      SetSourceVoltage / SetCompliance / ReadCurrent …
  widened Measurement)
```

Each also: `DeviceNames`, `GetDevice`, `GetState`, `IsConnected`,
`ConnectAsync` / `DisconnectAsync`, `StateChanged` — the common facet surface.

## Verification

Per device type, on the bench: the engine loads the config, connects the
device in `ConnectAllAsync` (count reported), the poll loop reports state, and
a facet call drives real hardware. Then a Layer 2 recipe line
(`lighting.main.setIntensity(channel: 0, percent: 50)`) runs end to end.

## Docs to update

`docs/layer1-hardware-engine-design.md` — add the new facets to the engine
surface. `docs/layer2-process-module-design.md` — note the extended
recipe/workflow vocabulary once Task 5 lands.
