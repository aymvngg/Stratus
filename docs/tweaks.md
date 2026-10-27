# Tweak reference

Every tweak in Stratus, with the exact registry keys, defaults and reasoning. The **Learn more** link on each tweak row in the app opens the matching section below.

- All values shown as `hive\key\ValueName = tweaked (default X)`.
- **Revert** always restores the recorded pre-change value from `%ProgramData%\Stratus\backups.json`, not a guessed default.
- Presets (Dashboard) only ever apply tweaks marked **Safe** or **Recommended**.

## Gaming

### game-dvr-off
**Disable Game DVR** - Safe, no restart.

- `HKCU\System\GameConfigStore\GameDVR_Enabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR = 0` (default 1)

Stops Windows from recording gameplay in the background in every game, removing constant capture overhead.

### gamebar-popups-off
**Disable Game Bar pop-ups** - Safe, no restart.

- `HKCU\Software\Microsoft\GameBar\ShowStartupPanel = 0` (default 1)
- `HKCU\Software\Microsoft\GameBar\UseNexusForGameBarEnabled = 0` (default 1)

Prevents the Game Bar (Win+G) overlay and its popups from appearing in games.

### game-mode-on
**Ensure Game Mode is on** - Safe, no restart.

- `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1` (default 1; a missing value also counts as on)

Windows prioritizes the foreground game. On by default in current builds; this makes it explicit.

### hags-on
**Hardware-Accelerated GPU Scheduling** - Recommended, restart required.

- `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode = 2` (default 1)

Lets the GPU manage its own memory scheduling, reducing latency on supported GPUs (NVIDIA GTX 10xx+, AMD RX 5000+). The tweak is hidden if your GPU/driver does not expose the key.

### fso-off
**Disable Fullscreen Optimizations** - Advanced, no restart.

- `HKCU\System\GameConfigStore\GameDVR_FSEBehaviorMode = 2` (default 0)
- `HKCU\System\GameConfigStore\GameDVR_HonorUserFSEBehaviorMode = 1` (default 0)
- `HKCU\System\GameConfigStore\GameDVR_DXGIHonorFSEBehaviorCompatible = 1` (default 0)

Stops Windows from converting exclusive fullscreen games to a borderless flip model. Restores true exclusive fullscreen (lower latency), but some games behave better with optimizations on. Per-game trade-off.

### game-priority-separation
**Foreground priority boost** - Recommended, no restart.

- `HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation = 38` (default 2)

0x26: short fixed time slices with a 3:1 quantum boost for the foreground app. The standard low-latency scheduler setting for games and audio work.

### game-mouse-accel-off
**Disable mouse acceleration** - Safe, no restart.

- `HKCU\Control Panel\Mouse\MouseSpeed = "0"` (default "1")
- `HKCU\Control Panel\Mouse\MouseThreshold1 = "0"` (default "6")
- `HKCU\Control Panel\Mouse\MouseThreshold2 = "0"` (default "10")

Turns off "Enhance pointer precision". Raw, predictable aim.

## Visuals

### transparency-off
**Disable transparency effects** - Safe, no restart.

- `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\EnableTransparency = 0` (default 1)

Turns off acrylic/blur effects across the shell. Frees a small amount of GPU and RAM.

### visualfx-performance
**Performance visual effects** - Recommended, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\VisualFXSetting = 2` (default 0)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\UserPreferencesMask = 90 12 03 80 10 00 00 00` (default 9E 3E 07 80 12 00 00 00)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAnimations = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ListviewAlphaSelect = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ListviewShadow = 0` (default 1)

"Best performance" visual effects: disables animations, keeps font smoothing and thumbnails.

### menu-delay
**Instant menu delay** - Safe, no restart.

- `HKCU\Control Panel\Desktop\MenuShowDelay = "0"` (default "400")

Context menus and drop-downs open instantly instead of after 400 ms.

### hover-time
**Fast tooltip hover time** - Safe, no restart.

- `HKCU\Control Panel\Desktop\MouseHoverTime = "8"` (default "400")

Tooltips appear almost instantly.

### visual-aero-shake-off
**Disable Aero Shake** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\DisallowShaking = 1` (default 0)

Stops shaking a window from minimizing everything else. Usually an accident, never useful.

### visual-widgets-off
**Hide Widgets** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDa = 0` (default 1)

Removes the taskbar Widgets button and stops the widget feed host running in the background.

### pref-taskbar-align-left
**Taskbar icons on the left** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAl = 0` (default 1)

Puts the taskbar buttons at the left edge instead of the Windows 11 centered layout.

### pref-search-icon-only
**Search as an icon** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\SearchboxTaskbarMode = 1` (default 2)

Shrinks the taskbar search box to a single icon. 0 hides search entirely, 1 is the icon, 2 the full box.

### pref-hide-taskview
**Hide Task View button** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowTaskViewButton = 0` (default 1)

Removes the Task View button. Win+Tab still opens it.

### pref-hide-chat-icon
**Hide the Chat icon** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarMn = 0` (default 1)

Removes the Chat/Teams consumer icon from the taskbar.

### pref-show-file-extensions
**Show file extensions** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\HideFileExt = 0` (default 1)

Explorer shows full file names such as `report.pdf` instead of `report`.

### pref-show-hidden-files
**Show hidden files** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Hidden = 1` (default 2)

Shows hidden files and folders in Explorer. 2 is the Windows default (hidden files off).

### pref-dark-mode
**Dark mode for apps and Windows** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme = 0` (default 1)

Pins both the app and the system theme to dark, so it no longer follows the light default.

### pref-clock-seconds
**Seconds in the taskbar clock** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowSecondsInSystemClock = 1` (default 0)

Shows `hh:mm:ss` in the notification area instead of `hh:mm`.

### pref-end-task-taskbar
**End task from the taskbar** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarEndTask = 1` (default 0)

Adds "End task" to the right-click menu of a taskbar button, the same action Task Manager offers.

### pref-numlock-on-boot
**NumLock on at boot** - Safe, no restart.

- `HKCU\Control Panel\Keyboard\InitialKeyboardIndicators = "2"` (default "0")

Turns NumLock on at the lock screen and keeps it on after sign-in.

### pref-sticky-keys-shortcut-off
**Sticky Keys shortcut off** - Safe, no restart.

- `HKCU\Control Panel\Accessibility\StickyKeys\Flags = "506"` (default "510")

Stops five quick Shift presses from opening the Sticky Keys prompt. The feature itself still works from Settings.

### visual-classic-context-menu
**Classic right-click menu** - Safe, no restart (Explorer restart needed to see it).

- `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32\(Default) = ""`

Restores the full Windows 10 style context menu instead of the compact Windows 11 one with "Show more options". Sign out or restart Explorer to apply; revert removes the override.

## Network

### net-nagle-off
**Disable Nagle's algorithm (TCP ACK)** - Recommended, restart required.

- Per physical interface: `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}\TcpAckFrequency = 1` and `TCPNoDelay = 1`

Nagle's algorithm coalesces small TCP packets (up to 200 ms delay) to save bandwidth. Disabling it trades a little throughput for noticeably lower latency in real-time games. Applied to all eligible physical adapters; virtual adapters are skipped.

### net-throttling-off
**Disable network throttling** - Recommended, no restart.

- `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NetworkThrottlingIndex = 0xFFFFFFFF` (default 10)

Removes the 10 packets/ms multimedia throttle that can cap network throughput during multimedia workloads, including games.

### net-games-task
**Boost "Games" multimedia task** - Recommended, no restart.

- `HKLM\...\Multimedia\SystemProfile\Tasks\Games\GPU Priority = 8` (default 2)
- `HKLM\...\Multimedia\SystemProfile\Tasks\Games\Priority = 6` (default 2)
- `HKLM\...\Multimedia\SystemProfile\Tasks\Games\Scheduling Category = "High"` (default "Medium")
- `HKLM\...\Multimedia\SystemProfile\Tasks\Games\SFIO Priority = "High"` (default "Normal")

Raises the priority Windows gives the "Games" multimedia task. The classic CPU/network latency tweak for game processes.

### net-delivery-optimization
**Stop Windows Update P2P uploads** - Recommended, no restart.

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config\DODownloadMode = 0` (default 1)

Updates download straight from Microsoft instead of being shared peer-to-peer. Stops background upload traffic competing with games and calls. Reference: [Delivery Optimization](https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference).

### net-system-responsiveness
**Reserve less CPU for multimedia** - Recommended, no restart.

- `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\SystemResponsiveness = 10` (default 20)

Halves the CPU slice the multimedia scheduler keeps for background audio/video work, leaving more for the foreground app.

## Services

Disabling sets the startup type to Disabled via `sc.exe config` and stops the service if it is running. The original startup type is backed up; revert restores it exactly.

### svc-diagtrack
**DiagTrack (Connected User Experiences and Telemetry)** - Recommended. Default startup: Automatic.

The telemetry service: collects and uploads usage data. Zero user-facing functionality is lost.

### svc-dmwappush
**dmwappushservice (WAP Push Message Routing)** - Recommended. Default startup: Manual.

Legacy SMS/WAP enterprise management channel. Not used on consumer PCs.

### svc-sysmain
**SysMain (Superfetch)** - Advanced. Default startup: Automatic.

Preloads frequently-used apps into RAM. Debated: can help HDD systems, often causes disk churn mid-game. Skip if you are on a slow HDD and like fast app launches.

### svc-remoteregistry
**RemoteRegistry** - Recommended. Default startup: Manual.

Allows remote machines to edit your registry. Nothing local uses it.

### svc-fax
**Fax** - Safe. Default startup: Manual.

Nobody faxes.

### svc-spooler
**Spooler (Print Spooler)** - Advanced. Default startup: Automatic.

Only disable if you never print. Also closes a known escalation path (PrintNightmare).

### svc-mapsbroker
**MapsBroker (Downloaded Maps Manager)** - Advanced. Default startup: Automatic (delayed).

Background service for the offline Maps app. Dead weight if you don't use Maps.

### svc-retaildemo
**RetailDemo** - Advanced. Default startup: Manual.

Shop-floor demo mode. Harmless to disable if present.

### svc-wer
**WerSvc (Windows Error Reporting)** - Recommended. Default startup: Manual.

Queues crash dumps and report uploads to Microsoft. Apps still crash the same way; you stop paying the background cost.

### svc-trx
**TrkWks (Distributed Link Tracking Client)** - Recommended. Default startup: Automatic.

Tracks shortcuts and file moves across volumes for roaming profiles. Pointless on a single-PC setup.

### svc-wmpnet
**WMPNetworkSvc (Media Player Network Sharing)** - Safe. Default startup: Manual.

Shares your media library over the network via UPnP. Only needed if you stream from this PC.

### svc-phonesvc
**PhoneSvc** - Safe. Default startup: Manual.

Brokers calls/SMS between Windows and a paired phone. Dead weight unless you link a phone.

## Privacy

### priv-telemetry
**Set telemetry to minimum** - Recommended.

- `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry = 0` (default 1)

On Pro+ this is "Security level" (minimal); on Home it reduces to the required-data minimum. Reference: [AllowTelemetry policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system#allowtelemetry).

### priv-adid
**Disable advertising ID** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo\Enabled = 0` (default 1)

Stops apps using a unique ID to build an ad profile of you.

### priv-activity
**Disable activity history and timeline upload** - Recommended.

- `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableActivityFeed = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\PublishUserActivities = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\UploadUserActivities = 0` (default 1)

Turns off activity feed collection and Microsoft-account upload of your app usage timeline.

### priv-suggestions
**Disable suggestions and silent app installs** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager`: `SubscribedContent-338388Enabled`, `SubscribedContent-338389Enabled`, `SubscribedContent-338393Enabled`, `SubscribedContent-310093Enabled`, `SilentInstalledAppsEnabled`, `SystemPaneSuggestionsEnabled` all `= 0` (default 1)

No suggested content in Start/Settings/lock screen, no auto-installing promoted apps.

### priv-copilot
**Disable Copilot** - Recommended.

- `HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot\TurnOffWindowsCopilot = 1` (default 0)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowCopilotButton = 0` (default 1)

Removes the Copilot taskbar entry and disables the built-in experience. Does not uninstall the Copilot app (use Debloat).

### priv-recall
**Disable Recall (AI snapshotting)** - Recommended.

- `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis = 1` (default 0)

Turns off Recall, which periodically screenshots your activity on Copilot+ PCs. Reference: [DisableAIDataAnalysis policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai).

### priv-input
**Disable inking and typing personalization** - Safe.

- `HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitInkCollection = 1` (default 0)
- `HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitTextCollection = 1` (default 0)

Stops Windows sending what you type and write to Microsoft.

### priv-feedback
**Disable feedback requests** - Safe.

- `HKCU\Software\Microsoft\Siuf\Rules\PeriodInNanoSeconds = 0` (default 0)

Zero feedback prompts per period. The value is absent by default, so this tweak's state reads from the key Stratus creates.

### priv-tailored
**Disable tailored experiences** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy\TailoredExperiencesWithDiagnosticDataEnabled = 0` (default 1)

Stops Microsoft using diagnostic data to personalize tips, ads and recommendations.

### priv-cloud-content
**Disable consumer features and app pushes** - Recommended.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\CloudContent\DisableWindowsConsumerFeatures = 1` (default 0)

No automatic installs of promoted Store apps, no suggested content in the shell. The single most effective anti-bloat policy.

### priv-sync-provider
**Disable OneDrive ads in File Explorer** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowSyncProviderNotifications = 0` (default 1)

Hides sync-provider banners in Explorer.

### priv-explorer-recent
**Hide recent and frequent folders** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowRecent = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowFrequent = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackDocs = 0` (default 1)

Clears Recent files and Frequent folders from Explorer Home and Quick Access.

### priv-app-tracking
**Stop app-launch tracking** - Safe.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackProgs = 0` (default 1)

Windows stops recording which programs you launch.

### priv-web-search
**Disable web results in Start search** - Recommended.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\CortanaConsent = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\DisableWebSearch = 1` (default 0)
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\ConnectedSearchUseWeb = 0` (default 1)

Start search becomes local-only: no Bing queries, no web suggestions. Faster, and no keystrokes leave the PC.

### pref-bing-search-off
**Bing results in Start search** - Safe, no restart.

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled = 0` (default 1)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\CortanaConsent = 0` (default 1)

A lighter version of the web search tweak: no web results in the Start menu, no policy hive writes. Use the full tweak when you also want the search policy locked at machine level.

### edge-policy-debloat
**Edge shopping and recommendations off** - Advanced, no restart.

- `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeShoppingAssistantEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\ShowRecommendationsEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\PersonalizationReportingEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\UserFeedbackAllowed = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeCollectionsEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeFollowEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\WebWidgetAllowed = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\SearchSuggestEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\AlternateErrorPagesEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeAssetDeliveryServiceEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\QuickSearchShowMiniMenu = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\WalletDonationEnabled = 0` (default 1)
- `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeDiscoverEnabled = 0` (default 1)

Removes the shopping assistant, recommendations, personalization reporting, feedback prompts, collections, followed creators, widgets, search suggestions, sponsored error pages, asset delivery, the mini menu, wallet donations and Discover. The browser still works normally.

### brave-policy-debloat
**Brave extras off** - Advanced, no restart.

- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveRewardsDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveWalletDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveVPNDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveAIChatEnabled = 0` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\TorDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\SyncDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\MetricsReportingEnabled = 0` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BackgroundModeEnabled = 0` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveNewsDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveTalkDisabled = 1` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveWebDiscoveryEnabled = 0` (default 0)
- `HKLM\SOFTWARE\Policies\BraveSoftware\Brave\BraveSpeedreaderEnabled = 0` (default 0)

Turns off Rewards, the wallet, the VPN, AI chat, Tor, sync, metrics, background mode, News, Talk, Web Discovery and Speedreader through policy. Brave itself stays installed.

### priv-wpbt-off
**Block firmware executables (WPBT)** - Advanced, no restart.

- `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\DisableWpbtExecution = 1` (default 0)

The Windows Platform Binary Table lets motherboard firmware drop an executable into Windows on every boot. Blocking it stops that path. Some vendor utilities rely on it.

### priv-reserved-storage-off
**Disable reserved storage** - Advanced, no restart.

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager\ShippedWithReserves = 0` (default 1)

Frees the several GB Windows reserves for updates and temporary files. Updates still need free disk space, so keep some headroom.

### priv-device-metadata-off
**No device metadata downloads** - Recommended, no restart.

- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata\PreventDeviceMetadataFromNetwork = 1` (default 0)

Stops Windows downloading vendor apps and metadata packages when a device is plugged in.

### priv-onedrive-remove
**Remove OneDrive** - Advanced, no restart.

Runs `OneDriveSetup.exe /uninstall` from `%SystemRoot%\SysWOW64` (or `System32` on 32-bit).

Removes the OneDrive app for this machine. Your local files are not deleted. Reverting runs the same setup program to reinstall it, then you sign in again.

## Power

### power-ultimate
**Ultimate Performance power plan** - Recommended.

Runs `powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61` (creates the plan if missing) and activates it. No aggressive CPU parking or display sleep timers; modern CPUs still downclock fine. Revert re-activates Balanced.

### power-hibernation-off
**Disable hibernation** - Advanced.

Runs `powercfg /h off`: deletes hiberfil.sys (~40% of RAM size) and disables Fast Startup. Revert runs `powercfg /h on`.

### power-fast-startup-off
**Disable Fast Startup** - Recommended.

- `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled = 0` (default 1)

A real shutdown instead of hybrid hibernate. Fixes "my settings reset" and dual-boot/update weirdness, and makes driver changes take effect on boot.

### power-throttling-off
**Disable power throttling (EcoQoS)** - Advanced, restart required.

- `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff = 1` (default 0)

Stops Windows silently capping background processes to efficiency cores. Useful for encode/compile/stream workloads; costs a little battery.
