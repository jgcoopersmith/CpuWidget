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
- **Drag any edge or corner** to resize. Widening buys more *history* rather than wider
  pixels: the graphs hold one sample per 3 px, so 300 px shows about 90 seconds and 900 px
  about 5 minutes. Making it taller gives the graphs more vertical room, so small
  movements in load and temperature are easier to read. Size is remembered.
  Range 84–1600 px wide, 84–1400 px tall.
- **Text scales with the window**, so fonts don't put a floor under how small it can get.
  Below ~62% scale the device name, clock and footer detail hide themselves — at that size
  they're unreadable anyway — leaving the labels, the load and temperature figures, and
  the graphs.
- **Right-click** for: always-on-top toggle, **°C / °F**, opacity, **colors**,
  *Start with Windows*, reset position, exit. Choices apply instantly and are remembered.
- **Colors** submenu sets the title, the CPU accent, the GPU accent and the background,
  each from a palette or via *Custom…*, which opens the system colour picker. The
  background keeps its alpha so the widget stays slightly translucent. *Reset colors*
  returns everything to the defaults. Temperature figures stay on their own
  green→amber→orange→red thresholds and aren't themeable.
- **Tray icon** is the thermostat dial. Hover for CPU load / temperature and GPU temperature;
  left-click hides/shows the widget; right-click for show-hide and exit.

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
| Footer left | CPU: peak temperature + reset. Then clock speed |
| Footer right | CPU: hottest core, package watts. GPU: hot spot, VRAM used, watts, then peak temperature + reset |

`XXX MAX` is the highest temperature seen since launch, or since it was last reset with the
`↻` button beside it. The CPU's sits at the far bottom-left, the GPU's at the bottom-right
past the watts. Resetting clears the peak, which then repopulates from the current reading.
Peaks are tracked in Celsius and displayed in whichever unit is selected.

Values only appear when the hardware exposes them; anything missing shows `--`.

## Troubleshooting

The widget writes `%APPDATA%\CpuWidget\log.txt` on every run, including a dump of every CPU
and GPU sensor it found on the first poll. If a reading shows `--`, that log says why.

`D:\Claude\CpuWidget\diag\SensorDump.exe` is a standalone console dump of the same data.
Don't run it while the widget is running — they share the same kernel driver.

## Icon

A round old-style thermostat dial with the needle swung to the top of the scale, into a red
"maxed out" band. It is drawn in code by `ThermostatIcon.Render`, which serves both the tray
icon (rendered at the shell's small-icon size at startup) and `app.ico`. To change the
artwork, edit `ThermostatIcon.cs` and regenerate the .ico:

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
