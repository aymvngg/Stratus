# Stratus — native Windows optimizer

WinUI 3 (unpackaged, self-contained, admin). 48 detection-driven tweaks with a live score, debloat, cleanup, and tools. No snake oil — every change is backed up and revertible.

## Run the app

Grab the zip from [Releases](../../releases), extract it anywhere, and run `Stratus.exe`.

Or build from source:

```
dotnet build Stratus.sln -c Release
.\Stratus\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\Stratus.exe
```

Double-click it (UAC prompt is expected — the manifest requires administrator).

## CLI (same engine, headless)

```
dotnet run --project Stratus.Cli -c Release -- list
dotnet run --project Stratus.Cli -c Release -- score
dotnet run --project Stratus.Cli -c Release -- info
dotnet run --project Stratus.Cli -c Release -- apply <tweak-id>
dotnet run --project Stratus.Cli -c Release -- revert <tweak-id>
```

(Run from an elevated terminal for changes to succeed.)

## Build

```
dotnet build Stratus.sln -c Release
```

## The 48 tweaks

| Category | Count | Examples |
|---|---|---|
| Gaming | 6 | Game DVR off, Game Mode on, HAGS, FSO off, foreground priority, mouse accel off |
| Visuals | 6 | transparency off, performance visual effects, instant menus, Aero Shake off, Widgets hidden |
| Network | 5 | Nagle off, throttling off, Games task priority, DO P2P off, SystemResponsiveness |
| Services | 11 | DiagTrack, SysMain, Spooler, Remote Registry, WER, TrkWks, PhoneSvc… |
| Privacy | 14 | telemetry, ad ID, activity, suggestions, Copilot, Recall, web search, consumer features… |
| Power | 4 | Ultimate Performance, hibernation off, Fast Startup off, EcoQoS throttling off |

Each tweak has a risk level (Safe / Recommended / Advanced), a weight used by the score, and a live `IsApplied` check. Tweaks that don't apply to your hardware (e.g. HAGS without an exposed `HwSchMode`) are excluded from the score denominator entirely.

## Where things live

- Tweak backups: `%ProgramData%\Stratus\backups.json` (every pre-change registry value / service startup state)
- Settings: `%ProgramData%\Stratus\settings.json`
- Restore points: created via `Checkpoint-Computer` before bulk operations (offered in "Apply recommended tweaks")

## Safety model

- The score reads real system state — it does not care who made a change.
- Every registry write is backed up first; Revert restores the exact original value.
- Advanced tweaks always show a confirmation dialog. Safe/Recommended do not.
- "Revert all tweaks" (Tools page) returns everything Stratus detects as applied to Windows defaults.
