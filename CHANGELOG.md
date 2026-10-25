# Changelog

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
