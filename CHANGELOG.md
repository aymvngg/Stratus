# Changelog

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
