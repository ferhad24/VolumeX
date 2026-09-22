<p align="center"><img src="assets/volumex-wordmark.png" alt="VolumeX" width="420"></p>

# VolumeX

System-wide audio amplification for Windows 10/11 — up to 500% — with a
look-ahead limiter that keeps boosted audio from clipping.

> VolumeX was called **Crescendo** up to 1.1.2. Internal names (the source
> folders, `Global\Crescendo.*`, `HKLM\SOFTWARE\Crescendo`, `ProgramData\Crescendo`)
> keep the old name so upgraded installations keep their settings and driver backups.

## How it works

VolumeX registers its own **Audio Processing Object** (APO) with the Windows
audio engine. The APO runs inside `audiodg.exe` as a *mode effect*, after
Windows has mixed every application, so one instance boosts everything with no
virtual cable, no driver and no added buffering.

```
apps ─► Windows mixer ─► [VolumeX APO] ─► driver ─► speakers
                          boost → subsonic HP → bass/treble → 10-band EQ
                          → mono/swap/balance → trim → look-ahead limiter → clamp
```

- **Limiter**: 5 ms look-ahead brickwall with an exact sliding-window minimum.
  The ceiling is a guarantee, not an approximation (tested at 500% on full-scale
  input). The look-ahead is reported to Windows as latency, so A/V sync holds.
- **Per-app boost**: the APO runs at the loudest boost any app asks for; every
  other app is scaled back down through the Windows mixer to the right ratio.
  Original mixer levels are restored on exit.
- **UI ↔ engine**: two shared-memory pages (`Global\Crescendo.*`) with a
  seqlock. The audio thread never blocks on the UI.

## Install

1. Download `VolumeX-Setup.exe` from [Releases](https://github.com/ferhad24/VolumeX/releases/latest) and run it
   (asks for admin; .NET is included). Later versions arrive through the in-app updater.
2. Press **Install engine**. This:
   - copies the APO to `C:\Program Files\VolumeX\Engine\`,
   - registers it and attaches it to the selected playback device,
   - sets `DisableProtectedAudioDG=1` so Windows loads an unsigned APO
     (this weakens the protected audio path some DRM playback uses),
   - restarts the Windows audio service once.
3. Turn **Boost on**.

Whatever the driver had in that effect slot is backed up under
`HKLM\SOFTWARE\Crescendo\Backup` and restored by **Devices → Remove completely**,
which also reverts `DisableProtectedAudioDG`.

If the engine never shows "Processing", switch **Devices → Placement** to
*Endpoint* and reinstall. Some Windows updates reset the APO policy; reinstalling
fixes it.

## Hotkeys

Global, and all of them can be re-recorded in **Settings → Hotkeys**.

| Keys | Action |
|---|---|
| `Ctrl` + `.` | boost up — a tap moves one step (default 25%), holding glides in 5% steps |
| `Ctrl` + `,` | boost down — same |
| `Ctrl` + `Alt` + `B` | boost on / off |
| `Ctrl` + `Alt` + `0` | back to 100% |

`Fn` cannot be part of a hotkey: the keyboard's firmware handles it and never
passes it to Windows. `Ctrl + .` / `Ctrl + ,` sit in the same place. Note that
they replace the same shortcuts in VS Code (Settings / Quick Fix) while
VolumeX is running.

## Build

Requires Visual Studio 2022 Build Tools (C++ + ATL), Windows SDK 10.0.26100 and
the .NET 8 SDK.

| Command | Does |
|---|---|
| `build\build-apo.cmd` | builds `artifacts\CrescendoApo.dll` |
| `build\run-tests.cmd` | DSP suite (21 checks) + COM suite (13 checks) |
| `build\publish.cmd` | icon → APO → tests → `dist\` (stops if a test fails) |
| `build\verify-engine.ps1` | after install: checks registry and proves the APO is processing audio inside audiodg by watching its heartbeat |

## Layout

```
src/Crescendo.Apo/        C++ APO (COM, DSP in dsp/)
  CrescendoAbi.h          shared-memory contract — mirrored in Interop/CrescendoAbi.cs
src/Crescendo.App/        WPF app (.NET 8)
  Interop/                shared memory, Core Audio COM, APO installer
  Services/               devices, sessions, engine, hotkeys, tray, settings
tests/                    native DSP and COM tests
```

Settings and per-device profiles: `C:\ProgramData\Crescendo\settings.json`.
Crash log: `C:\ProgramData\Crescendo\crescendo.log`.
