# Stratus specification

Stratus is a native Windows optimizer: WinUI 3 (unpackaged, self-contained, administrator manifest), .NET 8, C# 12. Every change it makes is backed up first and revertible. This document describes the architecture, the safety invariants and how to extend the app.

## Projects

| Project | Role |
|---|---|
| `Stratus.Core` | All logic: tweaks, services, stores, scoring. No UI dependency. |
| `Stratus` | WinUI 3 app: pages, view models, tray integration, Velopack updates. |
| `Stratus.Cli` | Headless front end over the same engine, used for automation and smoke tests. |
| `Stratus.Tray` | Tray helper process. |
| `Stratus.Tests` | xUnit tests for the pure logic. |

Dependency direction is one way: `Stratus` and `Stratus.Cli` reference `Stratus.Core`. `Stratus.Core` references no UI project.

## Layers

```
Views (XAML) -> ViewModels -> AppServices (composition root) -> Services / Tweaks -> Windows APIs
```

- `Stratus\ViewModels\AppServices.cs` is the composition root. It creates the stores and services once and exposes them as static properties. The CLI builds the same graph by hand.
- View models never call Windows APIs directly. They call a service and translate the result into a status message.
- Services return `(bool Success, string Message)` tuples or small records. They log through `Log` and never throw into the UI.
- `ProcessRunner` is the only place that starts child processes. `RegistryService` is the only place that reads or writes the registry for tweaks.

## Tweak model

`Stratus.Core\Tweaks\Tweak.cs` defines the contract:

```csharp
public abstract class Tweak
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public TweakCategory Category { get; }
    public RiskLevel Risk { get; init; }        // Safe, Recommended, Advanced
    public int Weight { get; init; } = 1;       // score weight
    public bool RequiresReboot { get; init; }
    public Func<bool>? Applicability { get; init; }

    public virtual bool IsApplicable => ...;
    public abstract bool IsApplied { get; }     // reads live system state
    public abstract TweakResult Apply();
    public abstract TweakResult Revert();
}
```

Three implementations:

- `RegistryTweak`: declarative list of `RegistryAction` (set or delete a value, with a default). `IsApplied` is true when every action matches. `Apply` backs up and writes every action, and rolls back the values written in the attempt if one fails.
- `ServiceTweak`: sets a Windows service startup type (`Disabled`, `Manual`, `Automatic`, `AutomaticDelayed`) with `sc.exe`, backs up the original start type, and reverts to the backup or a factory fallback.
- `DelegateTweak`: wraps three delegates for actions that are not a registry value or a service (for example removing OneDrive). Exceptions become `TweakResult.Fail`.

`TweakEngine` owns the catalog, raises `StateChanged`, records history, and provides bulk apply by risk level and by id list.

`TweakCatalog.Build(reg, power, settings, backup)` returns the full list. It is the single source of truth for what the app can change. Entries are grouped by category with banner comments.

### Rules for every tweak

1. `IsApplied` reads the machine, never a Stratus file. The score and the UI must reflect reality even if someone else changed the setting.
2. `Apply` writes a backup before the change. `Revert` restores the backup; `DefaultValue` is only a fallback when no backup exists.
3. A failure returns `TweakResult.Fail` with a human readable message. A partial failure must not leave the machine half changed when the tweak owns several values.
4. A description states what changes, the effect, and that revert exists. No marketing claims.
5. `Risk = Advanced` means the change can break a feature or is hard to notice. The UI asks for confirmation on Advanced only.

## Presets

`PresetCatalog` exposes `All` plus one property per preset. A `Preset` is an id, a name, a description, a glyph and a list of tweak ids. The Dashboard renders every entry in `All` automatically with live progress. Presets are curated sets, not a separate execution path: they call the same engine.

## Services

| Service | Responsibility |
|---|---|
| `RegistryService` | Backed-up registry reads and writes. |
| `ServiceTweak` / `SystemInfoService` | Service control and elevation checks. |
| `CleanupService` | Scans and deletes cache, temp and log targets. |
| `DebloatService` | UWP packages, provisioned packages, Win32 uninstall. |
| `OptionalFeatureService` | Lists, removes and enables Windows optional features and capabilities. |
| `StartupService` | Run keys and Startup folders, using the `StartupApproved` blob. |
| `ScheduledTaskService` | Known scheduled tasks through the Task Scheduler COM API. |
| `DnsService` / `DnsBenchmarkService` | DNS profiles (IPv4 and IPv6) and latency measurement. |
| `RestorePointService` | Creates, lists and deletes restore points. |
| `UpdateService` / `UpdatePauseService` / `UpdateResetService` | Update state, pause ownership, cache reset. |
| `TcpSettingsService` | Auto-tuning, RSS, timestamps. |
| `HealthScanService` | SFC and DISM health checks. |
| `ReportService` | Plain text environment report. |
| `ScoreService` | Weighted score from live tweak state. |
| `ProcessRunner` | Bounded child process execution with UTF-8 output. |
| `CommandLine` | Shared command line splitter. |
| `Log` | Daily log files under `%ProgramData%\Stratus\logs`. |

## Storage

All state lives under `%ProgramData%\Stratus` (see `AppPaths`):

| File | Content |
|---|---|
| `backups.json` | Pre-change value per tweak, scope and key. |
| `history.json` | Apply and revert timeline, capped at 500. |
| `score-history.json` | Score snapshots, capped at 500. |
| `settings.json` | Theme, tray, power plan and other preferences. |
| `tasks.json` | Original state of every scheduled task Stratus touched. |
| `startup.json` | Original state of every startup entry Stratus touched. |

Every store goes through `JsonFile`, which writes to a unique temp file and moves it over the real one, and quarantines an unreadable file as `.corrupt` instead of silently starting empty.

## UI

`MainWindow` hosts a `NavigationView`. Pages: Dashboard, Tweaks, Debloat, Cleanup, Startup, Tasks, History, Tools, Settings.

- Pages bind to a view model with `{x:Bind}`. Every page caches its navigation state where it makes sense.
- Long operations set `IsBusy`, show `BusyText`, disable their buttons and report through one `InfoBar`.
- Status severity follows the real outcome: Success for a completed action, Warning for a partial or no-op result, Error for a failure.
- Every async flow has a `catch` that logs and shows a message.

## CLI

```
Stratus.Cli list | score | info | presets | startup | tasks | cleanup | history | backups
Stratus.Cli apply <tweak-id> | revert <tweak-id>
Stratus.Cli apply --preset <preset-id> | revert --preset <preset-id>
Stratus.Cli dns-bench | report
```

Run it elevated for changes to succeed. It builds the same service graph as the app, so a CLI result is a valid check of the engine without the UI.

## Tests

`Stratus.Tests` covers the logic that does not need an elevated session:

- command line parsing and path resolution
- registry value comparison and normalization
- atomic stores, backup replacement and quarantine
- catalog integrity: unique ids, described entries, no duplicate registry action sets, every preset id resolves

Run:

```
dotnet build Stratus.sln -c Release
dotnet test Stratus.Tests\Stratus.Tests.csproj -c Release --no-build
```

CI runs the build, the tests and a CLI smoke test on every push and pull request.

## Release

Pushing a tag (`v1.7.0`) triggers `.github/workflows/release.yml`, which publishes the app, zips it, packs the Velopack installer and delta packages, and creates the GitHub release. The release notes come from the matching `CHANGELOG.md` section. Local builds report version 0.0.0; CI stamps the tag version.

## Extension guides

**Add a registry tweak:** add one `RegistryTweak` entry in `TweakCatalog.Build` inside the right category, with id, name, description, `RegistryAction` list, `Weight` and `Risk`. Add a test id only if another component references it.

**Add a one-shot action:** add a `DelegateTweak` with an `IsApplied` check that reads real state, an apply delegate and a revert delegate. Never run a downloaded script.

**Add a preset:** add a `Preset` to `PresetCatalog` and include it in `All`. Every id must exist in the catalog; the test suite enforces it.

**Add a DNS provider:** add a `DnsProfile` to `DnsService.Profiles` with IPv4, IPv6 and a label that includes the primary address. The Tools page and the benchmark pick it up automatically.

**Add a cleanup target:** add a `CleanupTarget` to `CleanupService.Targets`. Prefer `CacheDirNames` or `FileGlobs` over deleting a whole tree.

**Add a Windows feature:** add the feature name to `OptionalFeatureService` and a friendly name. Removal and enablement share the same list.
