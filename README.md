# CPU Widget

A small always-on-top desktop widget for Windows 11 showing live CPU **and GPU** usage,
temperature, clock speed and power, each with a 90-second history graph, plus a live tray icon.

The GPU section hides itself if no GPU sensors are found.

## Run

```
D:\Claude\CpuWidget\app\CpuWidget.exe
```

It requests Administrator (UAC prompt). This is required: temperatures come from the CPU's
MSRs via LibreHardwareMonitor's kernel driver, which won't load unelevated. Run it
unelevated and you'll get usage only — everything else shows `--`.

## Controls

- **Drag** anywhere on the widget to move it. Position is remembered.
- **Drag the left or right edge** to widen it. A faint bar appears on hover to mark the grip.
  Widening buys more *history* rather than wider pixels: the graphs hold one sample per
  3 px, so 300 px shows about 90 seconds and 900 px about 5 minutes. Width is remembered,
  and dragging the left edge keeps the right edge pinned. Range 260–1600 px.
- **Right-click** for: always-on-top toggle, **°C / °F**, opacity, *Start with Windows*,
  reset position, exit. The unit choice applies instantly and is remembered.
- **Tray icon** shows CPU% over a bar tinted by temperature. Left-click hides/shows the widget.

*Start with Windows* registers a scheduled task (`CpuWidget`, run at logon with highest
privileges) rather than a Run-key entry, so it starts without a UAC prompt each boot.

Settings live in `%APPDATA%\CpuWidget\settings.json`.

## Readout

Two labelled sections, **CPU** (blue) and **GPU** (violet), each laid out the same way:

| Element | Meaning |
| --- | --- |
| Accent number | Load, % |
| Coloured number | Temperature — green <60°, amber <75°, orange <90°, red above |
| Solid accent line | Load history (0–100%) |
| Dashed line | Temperature history (25–100°C) |
| Footer left | Clock speed |
| Footer right | CPU: hottest core, package watts. GPU: hot spot, VRAM used, watts |

Values only appear when the hardware exposes them; anything missing shows `--`.

## Troubleshooting

The widget writes `%APPDATA%\CpuWidget\log.txt` on every run, including a dump of every CPU
and GPU sensor it found on the first poll. If a reading shows `--`, that log says why.

`D:\Claude\CpuWidget\diag\SensorDump.exe` is a standalone console dump of the same data.
Don't run it while the widget is running — they share the same kernel driver.

## Icon

`app.ico` is a round old-style thermostat dial with the needle swung to the top of the
scale, into a red "maxed out" band. It is generated rather than hand-drawn — edit
`IconGen/Program.cs` and regenerate:

```
dotnet run --project IconGen -c Release -- app.ico
```

It writes 16/20/24/32/48/64/128/256 px frames. The small frames deliberately use a thinner
bezel and a wider scale band; at 16 px the detailed version collapses into a dark blob.

## Build

Requires the .NET 10 SDK.

```
dotnet publish D:\Claude\CpuWidget\CpuWidget.csproj -c Release -o D:\Claude\CpuWidget\app
```

## Notes

- `MSAcpi_ThermalZoneTemperature` (the driver-free WMI route) is access-denied on this
  machine and on most desktops reports a motherboard zone rather than CPU cores, so it
  isn't used.
- On a 14900KF the package sensor and the hottest P-core can differ by several degrees;
  both are shown.
