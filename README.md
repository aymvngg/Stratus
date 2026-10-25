# Stratus - native Windows optimizer

WinUI 3 (unpackaged, self-contained, admin). 48 detection-driven tweaks with a live score, debloat, cleanup, and tools. No snake oil - every change is backed up and revertible.

## Cleanup

15 targets, scanned live before anything is deleted:

- Temp: user temp, Windows temp
- Caches: DirectX / NVIDIA / AMD shader caches, thumbnail + icon cache, Edge / Chrome / Brave / Firefox caches
- System: Delivery Optimization cache, Windows Update leftovers, CBS/DISM logs, prefetch, previous Windows installation (Windows.old)
- Recycle Bin

Files that are in use are skipped and reported, never forced. Services that lock files (wuauserv, dosvc) are stopped for the clean and restarted after, only if they were running.

Tools > Maintenance also runs **DISM component store cleanup** (StartComponentCleanup), which removes superseded component versions. It can take 10-30 minutes.

## Run the app

Grab the zip from [Releases](../../releases), extract it anywhere, and run `Stratus.exe`.

Or build from source:

```
dotnet build Stratus.sln -c Release
.\Stratus\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\Stratus.exe
```

Double-click it (UAC prompt is expected - the manifest requires administrator).

## CLI (same engine, headless)

```
dotnet run --project Stratus.Cli -c Release -- list
dotnet run --project Stratus.Cli -c Release -- score
dotnet run --project Stratus.Cli -c Release -- info
dotnet run --project Stratus.Cli -c Release -- presets
dotnet run --project Stratus.Cli -c Release -- startup
dotnet run --project Stratus.Cli -c Release -- tasks
dotnet run --project Stratus.Cli -c Release -- cleanup
dotnet run --project Stratus.Cli -c Release -- history
dotnet run --project Stratus.Cli -c Release -- backups
dotnet run --project Stratus.Cli -c Release -- apply <tweak-id>
dotnet run --project Stratus.Cli -c Release -- revert <tweak-id>
```

(Run from an elevated terminal for changes to succeed.)

## Build

```
dotnet build Stratus.sln -c Release
```

## CI and releases

GitHub Actions builds the solution and runs a CLI smoke test on every push to `main`. Pushing a tag like `v1.1.0` publishes the app, zips it and attaches it to a GitHub release automatically.

## The 48 tweaks

| Category | Count | Examples |
|---|---|---|
| Gaming | 7 | Game DVR off, Game Mode on, HAGS, FSO off, foreground priority, mouse accel off |
| Visuals | 6 | transparency off, performance visual effects, instant menus, Aero Shake off, Widgets hidden |
| Network | 5 | Nagle off, throttling off, Games task priority, DO P2P off, SystemResponsiveness |
| Services | 12 | DiagTrack, SysMain, Spooler, Remote Registry, WER, TrkWks, PhoneSvc... |
| Privacy | 14 | telemetry, ad ID, activity, suggestions, Copilot, Recall, web search, consumer features... |
| Power | 4 | Ultimate Performance, hibernation off, Fast Startup off, EcoQoS throttling off |

Each tweak has a risk level (Safe / Recommended / Advanced), a weight used by the score, and a live `IsApplied` check. Tweaks that don't apply to your hardware (e.g. HAGS without an exposed `HwSchMode`) are excluded from the score denominator entirely.

Full reference with every registry key, default and reasoning: [docs/tweaks.md](docs/tweaks.md) (the "Learn more" link on each tweak row opens the matching section).

## Presets

The Dashboard ships three curated one-click sets: **Gaming PC**, **Balanced** and **Max Privacy**. Presets only ever apply Safe and Recommended tweaks; Advanced ones stay opt-in on the Tweaks page. Each preset card shows live progress read from the system, not from what the app changed.

## Startup manager

The Startup page lists everything that launches with Windows: the HKCU and HKLM `Run` keys (including the 32-bit view) and both Startup folders. Toggling uses the same mechanism as Task Manager: it writes the `StartupApproved` blob (02 = enabled, 03 = disabled plus a timestamp) and never touches the command itself. So the entry is not deleted, disabling is instantly reversible, and uninstalling the app later still leaves the original entry intact. "Reset changes" returns every entry Stratus touched to its exact previous state.

## Scheduled tasks

The Tasks page covers 16 well-known telemetry, inventory and feedback jobs (`Microsoft Compatibility Appraiser`, `Consolidator`, `UsbCeip`, `DmClient`, `QueueReporting`, `Device`, `WinSAT` and friends). It drives the Task Scheduler COM API directly, so it never parses localized `schtasks` output, and only tasks that exist on your machine are listed. Turning a task off disables it, it is never deleted, and the original state is stored in `tasks.json` so "Reset changes" restores it exactly.

## Score history

The Dashboard graphs the score over time from `%ProgramData%\Stratus\score-history.json`. A point is recorded only when the score actually changes (capped at 500), so the line stays readable.

## History and undo

Every apply and revert lands in `%ProgramData%\Stratus\history.json` (newest first, capped at 500 entries). The History page shows that timeline with an Undo button on changes still in effect, plus a viewer for every backed-up value (key path, value name, type, old value) with a Restore button per tweak.

## Where things live

- Tweak backups: `%ProgramData%\Stratus\backups.json` (every pre-change registry value / service startup state)
- Settings: `%ProgramData%\Stratus\settings.json`
- Logs: `%ProgramData%\Stratus\logs\` (one file per day, kept for 14 days)
- Restore points: created via `Checkpoint-Computer` before bulk operations (offered in "Apply recommended tweaks")

## Safety model

- The score reads real system state - it does not care who made a change.
- Every registry write is backed up first; Revert restores the exact original value.
- Advanced tweaks always show a confirmation dialog. Safe/Recommended do not.
- "Revert all tweaks" (Tools page) returns everything Stratus detects as applied to Windows defaults.
