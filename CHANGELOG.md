# Changelog

## v1.7.0

Coverage release: the catalog grows from 73 to 92 tweaks, six global presets run from the app or the CLI, DNS gains 11 providers with IPv6, and the Debloat and Tools pages learn new tricks.

### Tweaks
- **19 new tweaks** (73 to 92). Eleven taskbar and Explorer preferences: left-aligned taskbar, search icon only, hide Task view, hide the chat icon, show file extensions, show hidden files, dark mode, clock seconds, End task in the taskbar menu, NumLock at boot, and no Sticky Keys shortcut. Plus the classic right-click menu, Bing-free Start search, a 13-policy Edge debloat, a 12-policy Brave debloat, WPBT blocking, reserved storage off, device metadata downloads off, and OneDrive removal
- **Two new policy sets**: Edge and Brave entries turn off shopping, recommendations, rewards, wallet, VPN, AI chat, metrics and similar features through the vendor policy hives. Revert removes the policies again
- **OneDrive removal** is a one-shot action with an explicit warning: the app is removed, local files stay, and reverting reinstalls the app
- **No duplicate entries**: the catalog test that detects two tweaks writing the same values now runs on every push

### Presets
- **Six global sets**: Minimal, Standard, Gaming PC, Balanced, Max Privacy and Advanced. Advanced is built from Standard plus the rest, so the two can never drift apart
- **Runnable headless**: `stratus apply --preset standard` and `stratus revert --preset standard` apply or revert a whole set, skip what is unknown, not applicable or already in that state, and report each tweak plus a summary
- **Dashboard** lists all six with live progress and the pending count, no extra UI needed

### DNS
- **11 providers**: Cloudflare (plain, malware blocking, family), Google, Quad9 (filtered and unfiltered), OpenDNS (standard and family) and AdGuard (standard and family), plus automatic from the router
- **IPv6**: each provider carries its IPv6 pair. Apply sets IPv4 first, then IPv6 best effort, so an adapter with IPv6 unbound never breaks a working IPv4 setup. Detection now reads static IPv6 servers too
- **Benchmark** still measures the IPv4 pair of each provider and skips the automatic entry

### Windows features
- **Enable as well as remove**: the Debloat page now lists missing features next to installed ones. Windows Subsystem for Linux (with Virtual Machine Platform), Hyper-V, Windows Sandbox, .NET Framework 3.5 and the NFS client can be enabled in one click
- **Clear state per row**: each feature shows Installed or Not installed and offers only the action that applies
- **Restart note**: enabling reports that a restart may be needed to finish

### Tools
- **Fixes card**: point Windows time at pool.ntp.org, restore the Windows default time server, and reset and update the winget sources when winget misbehaves
- **Windows tools card**: 18 classic consoles (Computer management, Device manager, Services, Task scheduler, Disk management, Event viewer, Firewall, System properties and more) behind a fixed allowlist

### Docs and tests
- **SPEC.md and AGENTS.md** at the repo root: architecture, service map, storage layout, the tweak contract, safety invariants and contribution rules
- **README and docs/tweaks.md** updated for the new counts, presets, DNS providers, features and tools
- **33 tests**: the suite now also checks that every preset id resolves to a real tweak, that preset ids and names are unique, and that DNS profiles have valid addresses and IPv6 pairs
- **CI runs the tests** on every push, before the CLI smoke test

## v1.6.1

Correctness and reliability pass over the whole app: several tweaks did the wrong thing or reported the wrong outcome, and a few operations could overlap.

### Fixes
- **Service tweaks did the opposite of their description**: entries that promised "manual, starts on demand" actually disabled the service outright, which broke cloud saves, troubleshooters and on-demand starts. Each service entry now applies the start type its description promises, detection matches that target, and revert restores the exact original (or the Windows factory default)
- **Consumer features tweak was a no-op**: DisableWindowsConsumerFeatures was written to a registry path Windows ignores. It now also writes the policy hive where the setting is honored, so Start menu suggestions actually stop
- **CPU boost tweak de-powered laptops**: it set the boost mode on wall power only and reverted to "boost disabled" instead of the factory default. It now covers wall and battery power and reverts to the Windows default on both
- **Duplicate tweak removed**: "Open Game Bar using the controller button" existed twice with identical registry values; one entry is gone
- **Half-finished tweaks**: a multi-value tweak that failed partway stayed half applied. Apply now rolls back the values it already wrote and reports the failure
- **Nagle tweak**: re-applying it could overwrite the stored original values, and a failed revert still reported success. Both fixed
- **Wrong status colors**: Tools page failures (health scan, DNS benchmark, update and network resets) showed a green Success bar; the Scheduled tasks and Startup pages always reported Success regardless of outcome. Severity now follows the real result, including partial success
- **Overlapping operations**: Cleanup, Scheduled tasks and Debloat let a second click start while the first was still running. Buttons now disable during long operations
- **Restore point frequency**: if restore point creation failed once, Windows kept creating a point on every change until you noticed. The setting is now always restored
- **Windows Update pause**: Resume no longer deletes a pause that was set from Windows Settings instead of Stratus
- **Debloat removal scope**: when elevated, package removal now removes for all users, matching the all-users scan
- **Hyper-V removal warns** that it breaks WSL2 and Windows Sandbox before you can pick it
- **Scheduled task reset**: a failed enable or disable no longer marks the task as changed

### Reliability and performance
- **No more possible hang**: a child process keeping the output pipes open could freeze Stratus forever; the wait is now capped
- **French output fixed**: PowerShell output from French Windows no longer shows garbled accents
- **Cleanup is scoped and faster**: the NVIDIA and AMD targets now clean only the real driver cache folders (AMD's settings data is no longer touched), and size scans no longer stat every file twice
- **Atomic score history**: the dashboard score history is written atomically like every other data file, and two Stratus processes can no longer corrupt each other's saves
- **No phantom backups**: a registry write that failed mid-flight no longer leaves a restore entry for a value that never changed

### Tests
- **First test suite**: command line parsing, registry value handling, the atomic stores and catalog integrity (unique tweak ids, no duplicate actions) are now covered by 27 automated tests

## v1.6.0

Stratus reaches further into the machine: 26 new tweaks, four more cleanup targets, filters and bulk actions for scheduled tasks and startup, optional feature and provisioned app removal, and restore point, update pause and TCP tools.

### Tweaks
- **26 new tweaks** (45 to 71) across gaming, visuals, network, services, privacy and power: Game Bar startup and notifications, Xbox save and network services, animation and Spotlight effects, TCP timestamps, QoS reserved bandwidth, Wi-Fi Sense, SMB1, the Xbox auth, accessory, RRAS, DPS and UPnP host services, settings suggestions, Start recommendations, clipboard sync, Edge first run, location tracking, typing insights, USB selective suspend, modern standby and processor boost mode
- **Same contract everywhere**: every new entry detects the live state, backs up the old value on apply and puts it back on revert

### Cleanup
- **Four new targets** (15 to 19): application crash dumps, the Windows Error Reporting queue, kernel minidumps and live kernel reports. All four are unchecked by default because they hold diagnostic data
- **Recommended button**: selects the safe everyday set in one click instead of ticking targets by hand

### Scheduled tasks
- **Columns**: last run, next run and author are shown per task, so a task that never runs or belongs to a third-party app is easy to spot
- **Search and folder filter**: filter by name, path or author and narrow the list to one task folder
- **Presets**: one click selects a known group (telemetry, compatibility, feedback, diagnostics), and the checked count is shown before you disable anything
- **Bulk actions**: enable or disable every checked task at once, plus select all and select none

### Startup
- **More detail per entry**: publisher, executable path, file size and a startup impact hint based on the entry location and size
- **Scope filter**: split entries by user, machine and scheduled startup
- **Disable per scope**: disable only the user entries or only the machine entries instead of all of them
- **Open location**: jump straight to the executable or the Run key behind an entry

### Debloat
- **Optional features**: scan Windows optional features and capabilities and remove the ones you pick, with the same elevation and progress handling as package removal
- **Provisioned packages**: list the packages staged for new user accounts and deprovision them, so a removed app does not come back for the next profile
- **Silent uninstall**: classic Win32 apps now run their silent uninstall command when one is known instead of opening the vendor uninstaller and waiting
- **Scope filter and preview**: filter by app scope and review the recommended removal list before anything is touched

### Tools
- **Restore points**: list every restore point with its date and delete a single point without wiping the rest
- **Windows Update pause**: pause updates for a chosen number of days and resume them, with the current pause state shown
- **TCP settings**: receive window auto-tuning, receive-side scaling and TCP timestamps can be read, changed and reset from the app

## v1.5.2

Debloat removal no longer hangs: the slow DISM deprovision step is gone and every package operation is now capped.

### Fixes
- **Debloat hang**: removing recommended apps could sit on "Removing 1 of 9" for many minutes because each package ran `dism /Online /Remove-ProvisionedAppxPackage`, which waits on the Windows servicing stack. Removal now uses the deployment API only, deprovisions in the background with a 60 second cap, and caps each package at 90 seconds, so a stuck app reports a timeout instead of freezing the batch
- **Debloat progress**: the status line now names the app being removed ("Removing 3 of 9: Microsoft News...")
- **What's new**: release notes are shown as plain text instead of raw markdown headings, bullets and asterisks

## v1.5.1

Stratus now starts from its shortcuts, keeps your DNS picker, and reports failures instead of crashing.

### Fixes
- **Installer and shortcuts**: the app no longer demands administrator rights in its manifest, which made Windows refuse to launch it from Setup, the Start Menu and the Desktop shortcut with error 740. Stratus asks for elevation itself right after launch, so UAC appears once and the installed app starts normally
- **Sidebar version**: the footer shows the build you are actually running (or "Dev build") instead of a hardcoded v1.2
- **Administrator badge**: the footer chip no longer claims administrator permanently. It only appears as a warning if the app somehow runs without rights
- **DNS resolver picker**: the selection is restored from the adapters after a restart instead of showing blank, and the card shows the active resolver. Servers that are not a preset appear as "Custom (x.x.x.x)"
- **Update button**: "Restart to update" was disabled exactly when a downloaded update was waiting. It works now, and a downloaded update that was never applied is picked up on the next launch
- **SFC scan output**: `sfc /scannow` writes UTF-16, so its output was garbled and the pass/fail detection never matched. It is decoded correctly and the verdict is shown

### Reliability
- **Crash hardening**: every page action catches failures and shows a message instead of taking the app down
- **Atomic data files**: backups, history, settings and scheduled task state are written to a temp file and swapped in, so a crash mid write can no longer wipe your rollback data. Unreadable files are quarantined as `.corrupt` instead of silently starting empty
- **Revert safety**: a partially failed restore now reports what could not be put back and keeps the backups for a retry
- **History**: undo warns before deleting a value the tweak created, group restore asks for confirmation, and clearing the timeline also clears the search box
- **Cleanup guard**: refuses empty paths and drive roots before deleting anything, and removes empty folders deepest first
- **Debloat**: DISM cleanup runs off the UI thread, uninstall commands with spaces in their path are parsed correctly, and per app failure messages survive the refresh
- **Startup entries**: HKLM and 32 bit Run entries no longer share an id, so resetting one no longer reverts the other
- **Hibernation tweak**: detection reads the registry instead of localized `powercfg` text, so it works on French Windows
- **Logs**: a day file rolls to `.log.1` past 8 MB and rolled files are pruned with the rest
- **DNS benchmark**: measures secondary resolvers too

### Icon
- **New logo**: the ribbon S logo ships as a multi size app icon (16 to 256 px) and as the installer splash screen

## v1.5.0

Stratus is now installed, not unzipped: a real Setup.exe with Start Menu shortcuts, an uninstall entry, and delta updates that replace only what changed.

### Installer
- **Setup.exe**: one-click installer (Velopack). Installs per user under `%LocalAppData%\Stratus`, creates Start Menu and Desktop shortcuts, registers an Apps & Features entry and launches Stratus when done
- **Real application icon**: the Stratus badge now shows on the exe, shortcuts and taskbar instead of the default template icon

### Updates
- **Delta updates**: installed copies download only the differences between versions instead of the full 104 MB build; the updater falls back to a full package when deltas are not possible
- **Crash-safe swapping**: the update is applied by Velopack's Update.exe, which can replace files even while Stratus is closing; a downloaded-but-unapplied update is now picked up again on the next launch
- **Portable builds still work**: the zip stays available on every release; portable copies report "(portable)" and point to the Setup.exe instead of trying to self-update

## v1.4.1

### Fixes
- **Reboot banner false positive**: the "Restart required" banner could stay visible after a reboot when Windows kept a leftover file rename operation (the Gaming Services proxy DLL re-registers its rename on every boot). Empty and known self-perpetuating rename entries are now ignored; the banner only appears for real servicing, Windows Update or file rename operations

## v1.4.0

Stratus now updates itself: it checks GitHub once a day, downloads a newer build in the background and swaps it in on the next restart.

### Updates
- **Update checker**: the Tools page shows the running version, checks GitHub on demand and reports when you are already on the latest release
- **Background download**: a newer release is fetched silently into `ProgramData\Stratus\updates`, with a progress bar when you start the check yourself
- **Restart to update**: one button confirms, closes Stratus, replaces its own files and starts the new version. The previous install is kept as `Stratus.old-<timestamp>` until the next launch, then removed
- **Release notes in the app**: the "What's new" section shows the notes published with the release
- **Automatic checks** can be switched off; the daily check is throttled to one request per 24 hours

### Packaging
- Release builds are stamped with the tag version, so the app knows what it is running. Local builds report a development version and skip automatic checks

## v1.3.0

Advanced tools: the repair and diagnostic actions that used to need a dozen terminal commands, in one place.

### Tools
- **Health scan**: run SFC, DISM component store scan and DISM repair from the app, with a plain-language result line and the raw output one click away. Marker matching covers English and French Windows output
- **Windows Update reset**: stops the update services, moves `SoftwareDistribution` and `catroot2` aside with a timestamp, restarts the services. Nothing is deleted
- **Network reset**: winsock reset, TCP/IP reset, DNS cache flush and DNS re-registration, with an explicit restart note
- **DNS benchmark**: sends live UDP queries straight to Cloudflare, Google and Quad9, three each, and ranks them by average round trip. No PowerShell, no system cache
- **System report**: writes a markdown snapshot (score, hardware, per-category breakdown, applied and pending tweaks) to `ProgramData\Stratus\reports`

### CLI
- `stratus dns-bench` prints the resolver ranking
- `stratus report` writes the same markdown report headlessly

## v1.2.0

A full interface revamp: the same engine and the same revertible changes, in a shell that finally looks the part.

### Dashboard
- Gradient hero with the live score ring, the system summary and four stat tiles: applied, pending, restart and session
- Restart banner appears only when Windows actually flags a pending reboot, with a **Restart now** button
- Category scores now use a fixed colour per category instead of a health colour
- **Apply recommended** shows how many tweaks it will apply and disables itself when nothing is pending
- The preset review dialog lists the tweaks that would actually change

### Tweaks
- Categories are expandable sections with a colour dot, an applied counter and a per-category **Apply safe** button
- Rows keep the risk, restart and N/A chips; the row stripe now carries the category colour
- Lists are virtualized, so long categories scroll smoothly

### Debloat and History
- Search boxes filter apps by name or publisher, and changes by tweak name or detail

### Cleanup
- A session chip shows everything freed since the app started

### Shell
- Navigation is grouped into Optimize and Maintain sections
- Page transitions animate on navigation
- Window size, position and maximized state are remembered between launches and clamped to the current screen setup

### Fixes
- Restart detection reads CBS, Windows Update and pending file renames directly from the registry

## v1.1.0

The first feature update: three new pages, presets, a bigger cleanup engine, and diagnostics.

### Presets (Dashboard)
- Three one-click presets: **Gaming PC**, **Balanced**, **Max Privacy**
- Each card shows live progress (x of y applied) and only contains Safe or Recommended tweaks
- Apply flow offers a restore point first, same as applying recommended tweaks

### History and undo (new History page)
- Every apply and revert is recorded with its result, newest first
- **Undo** button on any applied change
- Backup viewer: every stored registry value and service state, grouped by tweak, with per-tweak **Restore**
- History is capped at 500 entries and stored in `%ProgramData%\Stratus\history.json`

### Startup manager (new Startup page)
- Enable or disable startup apps from registry Run keys (current user, all users, 32-bit) and Startup folders
- Uses the same StartupApproved mechanism as Task Manager, so both stay in sync
- The launch command is never deleted, so every change is reversible; **Reset changes** restores the exact previous states

### Scheduled tasks (new Tasks page)
- Disable known telemetry and inventory scheduled tasks (Compatibility Appraiser, CEIP, Feedback, WER and more)
- Tasks are disabled, not deleted; **Reset changes** restores them

### Cleanup expansion
- 15 targets, up from 9: added Thumbnails, Edge / Chrome / Brave / Firefox caches, Delivery Optimization cache and Windows.old
- Browser cleaning only deletes folders literally named Cache, Code Cache, GPUCache and similar: profiles, history and logins are never touched
- Services (Windows Update, Delivery Optimization) are stopped only if running and restarted after cleaning
- New CLI command `cleanup` shows what is reclaimable without deleting anything

### Component store cleanup (Tools)
- DISM `StartComponentCleanup` behind a confirm dialog: clears superseded update components, often several GB
- `/ResetBase` is intentionally not used, so updates remain uninstallable

### Score history graph (Dashboard)
- Score over time, drawn as a line and area chart in your current health colour
- A point is recorded only when the score changes; last 80 points shown, 500 kept

### Diagnostics
- File logging to `%ProgramData%\Stratus\logs` (daily files, kept 14 days): app and CLI starts, every tweak result, cleanup totals, debloat removals, DNS changes, restore points
- **Open logs** button in Tools > Maintenance

### Tweaks documentation
- Every tweak row has a **Learn more** link to its exact section in `docs/tweaks.md`

### Fixes
- Restore point creation failed with a PowerShell quoting error: all PowerShell calls now use EncodedCommand and are immune to command-line quoting
- Dashboard, Tools and History pages now align with the 32px margin used everywhere else
- Custom title bar: the window draws its own title area over the Mica backdrop

### CLI
- New commands: `presets`, `startup`, `tasks`, `history`, `backups`, `cleanup`
- `apply` / `revert` unchanged; run from an elevated terminal

## v1.0.0

Initial release.

- 48 detection-driven tweaks across Gaming, Visuals, Network, Services, Privacy and Power
- Live score computed from real system state, with per-category breakdown
- Debloat: Microsoft bloatware, other Microsoft apps, third-party Store apps, desktop programs
- Cleanup: 9 targets including temp, shader caches, Windows Error Reporting, Windows Update downloads
- Tools: power plans (incl. Ultimate Performance), DNS profiles, restore points, revert all
- Every registry write and service change backed up to `%ProgramData%\Stratus\backups.json` first
- CLI: `list`, `score`, `info`, `apply`, `revert`
