# Stratus - native Windows optimizer

WinUI 3 (unpackaged, self-contained, admin). 92 detection-driven tweaks with a live score, debloat, cleanup, and tools. No snake oil - every change is backed up and revertible.

## Cleanup

19 targets, scanned live before anything is deleted:

- Temp: user temp, Windows temp
- Caches: DirectX / NVIDIA / AMD driver caches (only the cache folders, never settings), thumbnail + icon cache, Edge / Chrome / Brave / Firefox caches
- System: Delivery Optimization cache, Windows Update leftovers, CBS/DISM logs, prefetch, previous Windows installation (Windows.old)
- Diagnostics: application crash dumps, Windows Error Reporting queue, kernel minidumps, live kernel reports (all unchecked by default)
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
dotnet run --project Stratus.Cli -c Release -- apply --preset <preset-id>
dotnet run --project Stratus.Cli -c Release -- revert --preset <preset-id>
dotnet run --project Stratus.Cli -c Release -- docgen
dotnet run --project Stratus.Cli -c Release -- docgen --json
dotnet run --project Stratus.Cli -c Release -- export
dotnet run --project Stratus.Cli -c Release -- import <profile.json> --dry-run
```

(Run from an elevated terminal for changes to succeed.)

## Build

```
dotnet build Stratus.sln -c Release
```

## CI and releases

GitHub Actions builds the solution, runs the unit tests and a CLI smoke test on every push to `main`. Pushing a tag like `v1.7.0` publishes the app, zips it and attaches it to a GitHub release automatically.

## The 92 tweaks

| Category | Count | Examples |
|---|---|---|
| Gaming | 8 | Game DVR off, Game Mode on, HAGS, FSO off, foreground priority, mouse accel off |
| Visuals | 22 | transparency off, performance visual effects, instant menus, Aero Shake off, Widgets hidden, taskbar and Explorer preferences, classic right-click menu |
| Network | 9 | Nagle off, throttling off, Games task priority, DO P2P off, SystemResponsiveness |
| Services | 19 | DiagTrack, SysMain, Spooler, Remote Registry, WER, TrkWks, PhoneSvc... |
| Privacy | 27 | telemetry, ad ID, activity, suggestions, Copilot, Recall, web search, consumer features, Edge and Brave policies, WPBT, reserved storage, OneDrive removal |
| Power | 7 | Ultimate Performance, hibernation off, Fast Startup off, EcoQoS throttling off |

Each tweak has a risk level (Safe / Recommended / Advanced), a weight used by the score, and a live `IsApplied` check. Tweaks that don't apply to your hardware (e.g. HAGS without an exposed `HwSchMode`) are excluded from the score denominator entirely.

Full reference with every registry key, default and reasoning: [docs/tweaks.md](docs/tweaks.md) (the "Learn more" link on each tweak row opens the matching section).

## Presets

The Dashboard ships six curated one-click sets: **Minimal**, **Standard**, **Gaming PC**, **Balanced**, **Max Privacy** and **Advanced**. Each card shows live progress read from the system, not from what the app changed, and the same presets run headless through the CLI. Advanced presets include Advanced-risk entries, which still ask for confirmation in the app.

## Startup manager

The Startup page lists everything that launches with Windows: the HKCU and HKLM `Run` keys (including the 32-bit view) and both Startup folders. Toggling uses the same mechanism as Task Manager: it writes the `StartupApproved` blob (02 = enabled, 03 = disabled plus a timestamp) and never touches the command itself. So the entry is not deleted, disabling is instantly reversible, and uninstalling the app later still leaves the original entry intact. "Reset changes" returns every entry Stratus touched to its exact previous state.

## Scheduled tasks

The Tasks page covers 16 well-known telemetry, inventory and feedback jobs (`Microsoft Compatibility Appraiser`, `Consolidator`, `UsbCeip`, `DmClient`, `QueueReporting`, `Device`, `WinSAT` and friends). It drives the Task Scheduler COM API directly, so it never parses localized `schtasks` output, and only tasks that exist on your machine are listed. Turning a task off disables it, it is never deleted, and the original state is stored in `tasks.json` so "Reset changes" restores it exactly.

## Windows features

The Debloat page lists optional features and capabilities in both directions: installed ones can be removed, missing ones can be enabled. Enable covers Windows Subsystem for Linux (with Virtual Machine Platform), Hyper-V, Windows Sandbox, .NET Framework 3.5 and the NFS client. Hyper-V carries a warning because removing it breaks WSL2 and Windows Sandbox.

## Install apps

The Install page drives winget from a curated catalog of 68 apps across browsers, communication, development, media, productivity, utilities and gaming. Search by name or filter by category, install or upgrade a single app, remove one, or run "Upgrade installed" to walk every catalog app that is currently installed. Installed state comes from the `winget export` JSON, never from parsing localized command output, and "already installed" or "already up to date" are reported as success rather than as errors. New apps are one `AppEntry` in `AppCatalog`, with no install logic in the page.

## Profiles

The Profile page saves every selection the app knows about into one versioned JSON file: applied tweaks, disabled startup entries and scheduled tasks, enabled Windows features, apps picked on Install, the cleanup selection, DNS profile, power plan and TCP settings. Import the file on another machine to reproduce the setup. Importing always shows a preview first, with each item marked "Will change" or "Skipped" and the reason, and it is additive only: it applies what is missing and never reverts a tweak, re-enables a task, uninstalls an app or removes a feature. A file written by a newer Stratus is refused with a clear message instead of being half read. The same file works headless through `stratus export` and `stratus import`.

## DNS and network tools

Tools offers 11 DNS profiles with IPv4 and IPv6 addresses (Cloudflare, Google, Quad9, OpenDNS, AdGuard, plus their malware and family variants), a latency benchmark, and a reset back to your router's servers. IPv6 is applied best effort: an adapter with IPv6 unbound does not fail the change.

## Tools

Beyond tweaks and managers, Tools has one-click fixes (point Windows time at `pool.ntp.org` or restore the Windows default, repair the winget sources), a launcher for 18 classic Windows consoles, restore point management, Windows Update pause, TCP settings and DISM component cleanup.

## Documentation

- [SPEC.md](SPEC.md) describes the projects, layers, services and the tweak contract.
- [AGENTS.md](AGENTS.md) holds the build, test and safety rules for contributors (human or AI).
- [docs/reference.md](docs/reference.md) is generated from the compiled catalog by `stratus docgen`; `stratus docgen --json` writes the same metadata as `docs/catalog.json`.

## Score history

The Dashboard graphs the score over time from `%ProgramData%\Stratus\score-history.json`. A point is recorded only when the score actually changes (capped at 500), so the line stays readable.

## History and undo

Every apply and revert lands in `%ProgramData%\Stratus\history.json` (newest first, capped at 500 entries). The History page shows that timeline with an Undo button on changes still in effect, plus a viewer for every backed-up value (key path, value name, type, old value) with a Restore button per tweak.

## Tray and theme

- Closing the window hides Stratus in the notification area (toggle in Tools > Preferences); the tray tooltip shows the live score. Use Exit in the tray menu to quit for real.
- Theme follows your system by default; Dark or Light can be pinned in Tools > Preferences.

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
