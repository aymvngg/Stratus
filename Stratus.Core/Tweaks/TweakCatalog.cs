using Microsoft.Win32;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;

namespace Stratus.Core.Tweaks;

/// <summary>The full catalog of tweaks. Every entry is reversible and detection-driven.</summary>
public static class TweakCatalog
{
    public static List<Tweak> Build(RegistryService reg, PowerPlanService power, SettingsStore settings, IBackupStore backup)
    {
        var tweaks = new List<Tweak>();

        // ---------------- Gaming ----------------

        tweaks.Add(new RegistryTweak("game-dvr-off", "Disable Game DVR",
            "Stops Windows from recording your gameplay in the background (GameDVR_Enabled=0 + policy AllowGameDVR=0). Removes constant background capture overhead in every game.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, 1)
            })
        { Weight = 5, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("gamebar-popups-off", "Disable Game Bar pop-ups",
            "Prevents the Game Bar (Win+G) overlay and its 'how do you want to open this' popups from appearing in games.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "ShowStartupPanel", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("game-mode-on", "Ensure Game Mode is on",
            "Windows prioritizes the foreground game (fewer background tasks while gaming). On by default in current builds - this makes it explicit.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 1, missingMeansApplied: true)
            })
        { Weight = 2, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("hags-on", "Hardware-Accelerated GPU Scheduling",
            "Lets the GPU manage its own memory scheduling (HwSchMode=2). Reduces latency on supported GPUs (NVIDIA GTX 10xx+, AMD RX 5000+). Not shown if your GPU/driver doesn't expose it.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)
            })
        {
            Weight = 3,
            RequiresReboot = true,
            Risk = RiskLevel.Recommended,
            Applicability = () => reg.Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode").Exists
        });

        tweaks.Add(new RegistryTweak("fso-off", "Disable Fullscreen Optimizations",
            "Stops Windows from converting exclusive fullscreen games to borderless flip-model. Restores true exclusive fullscreen (lower latency, correct alt-tab behavior per game).",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEBehaviorCompatible", 1, 0)
            })
        { Weight = 2, Risk = RiskLevel.Advanced });

        tweaks.Add(new RegistryTweak("game-priority-separation", "Foreground priority boost",
            "Win32PrioritySeparation=38 (0x26): short fixed time slices with a 3:1 boost for the foreground app. The standard low-latency scheduler setting for games and audio work.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38, 2)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("game-mouse-accel-off", "Disable mouse acceleration",
            "Turns off 'Enhance pointer precision' (MouseSpeed=0, thresholds=0). Raw, predictable aim - the first thing any FPS player changes.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", "1"),
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", "6"),
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", "10")
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("game-gamebar-startup-off", "Keep the Game Bar out of the way",
            "Stops the Game Bar panel from opening at sign-in and from taking over the Xbox button. Press Win+G when you actually want it.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "ShowStartupPanel", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("game-gamebar-notifications-off", "Silence Game Bar notifications",
            "Turns off the achievement and recording popups that can steal focus and drop frames in full-screen games.",
            TweakCategory.Gaming, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Microsoft.XboxGamingOverlay", "Enabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new ServiceTweak("game-xbox-save-service", "Park the Xbox save sync service",
            "Sets the Xbox game save service to manual. Saves still work locally; cloud sync starts again the next time a game needs it.",
            "XblGameSave", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("game-xbox-net-service", "Park the Xbox networking service",
            "Sets the Xbox Live networking service to manual. Only needed for Xbox multiplayer and the Xbox app, which restart it on demand.",
            "XboxNetApiSvc", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        // ---------------- Visuals ----------------
        tweaks.Add(new RegistryTweak("transparency-off", "Disable transparency effects",
            "Turns off acrylic/blur effects across the shell. Frees a small amount of GPU and RAM on low-end systems.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)
            })
        { Weight = 2, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visualfx-performance", "Performance visual effects",
            "Sets 'Best performance' visual effects: disables window/taskbar animations and selection rectangle fade, keeps font smoothing and thumbnails (known-good UserPreferencesMask 90 12 03 80 10 00 00 00).",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2, 0),
                RegistryAction.SetBinary(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "UserPreferencesMask",
                    [0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00],
                    [0x9E, 0x3E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00]),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow", 0, 1)
            })
        { Weight = 3, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("menu-delay", "Instant menu delay",
            "MenuShowDelay 400ms → 0ms: context menus and drop-downs open instantly.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", "400")
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("hover-time", "Fast tooltip hover time",
            "MouseHoverTime 400ms → 8ms: tooltips appear almost instantly. Purely a responsiveness nicety.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MouseHoverTime", "8", "400")
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-aero-shake-off", "Disable Aero Shake",
            "Stops grabbing and shaking a window from minimizing everything else. Usually an accident, never useful.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisallowShaking", 1, 0)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-widgets-off", "Hide Widgets",
            "Removes the taskbar Widgets button and stops the widget feed host from running in the background (TaskbarDa=0).",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-animations-off", "Cut menu and taskbar animations",
            "Removes the fade and slide animations from menus and taskbar buttons. Windows feels immediate, which matters most on older hardware.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetString(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuAnimation", "0", "1"),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-spotlight-off", "Turn off lock screen Spotlight",
            "Stops Windows from downloading rotating lock screen pictures. Saves a little background network and disk work, and the lock screen stays predictable.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-notification-banners-off", "Hide notification banners",
            "Keeps notifications in the Action Center but stops them from sliding in over whatever you are doing. Focus assist style, without the schedule.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("visual-snap-flyout-off", "Turn off the snap layouts flyout",
            "Stops the snap layouts panel from appearing when you hover a window's maximize button. Snapping with the keyboard and drag still works.",
            TweakCategory.Visuals, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableSnapAssistFlyout", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        // ---------------- Network ----------------

        tweaks.Add(NagleTweak.Create(reg));

        tweaks.Add(new RegistryTweak("net-throttling-off", "Disable network throttling",
            "NetworkThrottlingIndex=0xFFFFFFFF removes the 10 packets/ms multimedia throttle that can cap network throughput during multimedia workloads (incl. games).",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("net-games-task", "Boost 'Games' multimedia task",
            "Raises the priority Windows gives the 'Games' multimedia task (SystemProfile\\Tasks\\Games): Priority 6, GPU Priority 8, High scheduling. The classic CPU/network latency tweak for game processes.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, 2),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6, 2),
                RegistryAction.SetString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "High", "Medium"),
                RegistryAction.SetString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "SFIO Priority", "High", "Normal")
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("net-delivery-optimization", "Stop Windows Update P2P uploads",
            "DODownloadMode=0: updates download straight from Microsoft instead of being shared peer-to-peer. Stops background upload traffic that competes with games and calls.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0, 1)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("net-system-responsiveness", "Reserve less CPU for multimedia",
            "SystemResponsiveness=10 (default 20): halves the CPU slice the multimedia scheduler keeps for background audio/video work, leaving more for the foreground app.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, 20)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("net-tcp-timestamps-off", "Drop TCP timestamps",
            "Keeps window scaling but stops adding a timestamp to every TCP segment. Slightly less per-packet overhead on fast links, no effect on normal browsing.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "Tcp1323Opts", 1, 3)
            })
        { Weight = 1, Risk = RiskLevel.Recommended, RequiresReboot = true });

        tweaks.Add(new RegistryTweak("net-qos-bandwidth", "Release the reserved QoS bandwidth",
            "Windows keeps 20 percent of the link for QoS by default. Setting the limit to zero hands that slice back to normal traffic; QoS still works when an app asks for it.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0, 20)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("net-wifi-sense-off", "Turn off Wi-Fi Sense sharing",
            "Stops Windows from offering to share Wi-Fi credentials with contacts and from pulling suggested hotspots. Nothing you joined is forgotten.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config", "AutoConnectAllowedOEM", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("net-smb1-off", "Disable the SMB1 file sharing protocol",
            "SMB1 is the 1990s file sharing protocol behind WannaCry. Modern Windows and NAS boxes use SMB2 or SMB3, so turning it off closes an attack surface with no everyday cost.",
            TweakCategory.Network, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", 0, 1)
            })
        { Weight = 2, Risk = RiskLevel.Recommended, RequiresReboot = true });

        // ---------------- Services ----------------

        tweaks.Add(new ServiceTweak("svc-diagtrack", "Disable DiagTrack (Connected User Experiences and Telemetry)",
            "The telemetry service. Collects and uploads usage data to Microsoft. Zero user-facing functionality is lost.",
            "DiagTrack", ServiceStartupType.Automatic, backup, weight: 3)
        { Risk = RiskLevel.Recommended });

        tweaks.Add(new ServiceTweak("svc-dmwappush", "Disable WAP Push Message Routing (dmwappushservice)",
            "Legacy SMS/WAP enterprise management channel. Not used on consumer PCs.",
            "dmwappushservice", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Recommended });

        tweaks.Add(new ServiceTweak("svc-sysmain", "Disable SysMain (Superfetch)",
            "Preloads frequently-used apps into RAM. Debated: can help HDD systems, often causes disk churn mid-game. Skip if you're on a slow HDD and like fast app launches.",
            "SysMain", ServiceStartupType.Automatic, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-remoteregistry", "Disable Remote Registry",
            "Allows remote machines to edit your registry. Disabled by default attack-surface-wise on most setups; nothing local uses it.",
            "RemoteRegistry", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Recommended });

        tweaks.Add(new ServiceTweak("svc-fax", "Disable Fax",
            "Nobody faxes. Not even in 2026.",
            "Fax", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Safe });

        tweaks.Add(new ServiceTweak("svc-spooler", "Disable Print Spooler",
            "Only choose this if you never print. Also closes a well-known escalation path (PrintNightmare).",
            "Spooler", ServiceStartupType.Automatic, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-mapsbroker", "Disable Downloaded Maps Manager",
            "Background service for the offline Maps app. If you don't use Maps, it's dead weight.",
            "MapsBroker", ServiceStartupType.AutomaticDelayed, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-retaildemo", "Disable Retail Demo Service",
            "Shop-floor demo mode for retail stores. Removed on retail machines by the store; harmless to disable if present.",
            "RetailDemo", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-wer", "Disable Windows Error Reporting",
            "Sends crash dumps to Microsoft and queues report uploads. Apps still crash the same way; you just stop paying the background upload and disk cost.",
            "WerSvc", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Recommended });

        tweaks.Add(new ServiceTweak("svc-trx", "Disable Distributed Link Tracking Client",
            "Tracks shortcuts and file moves across volumes for roaming profiles. Pointless on a single-PC setup.",
            "TrkWks", ServiceStartupType.Automatic, backup, weight: 1)
        { Risk = RiskLevel.Recommended });

        tweaks.Add(new ServiceTweak("svc-wmpnet", "Disable Media Player Network Sharing",
            "Shares your media library over the network via UPnP. Only needed if you stream from this PC to a TV or console.",
            "WMPNetworkSvc", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Safe });

        tweaks.Add(new ServiceTweak("svc-phonesvc", "Disable Phone Service",
            "Brokers calls and SMS between Windows and a paired phone. Dead weight unless you actually link a phone to the PC.",
            "PhoneSvc", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Safe });

        tweaks.Add(new ServiceTweak("svc-xbox-auth", "Park the Xbox Live auth service",
            "Sets the Xbox Live authentication service to manual. Signing into the Xbox app or a Game Pass title starts it again on demand.",
            "XblAuthManager", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-xbox-accessory", "Park the Xbox accessory service",
            "Sets the Xbox controller accessory service to manual. Wired and Bluetooth controllers still work; the service starts when a game asks for it.",
            "XboxGipSvc", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-rras", "Disable the legacy routing service",
            "Turns off Routing and Remote Access, the service behind dial-up and old VPN setups. Home networks and modern VPN clients do not use it.",
            "RemoteAccess", ServiceStartupType.Disabled, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-dps", "Park the diagnostic policy service",
            "Sets the Diagnostic Policy Service to manual. Windows troubleshooting wizards still work; the always-on background diagnostics stop.",
            "DPS", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        tweaks.Add(new ServiceTweak("svc-upnphost", "Park the UPnP device host",
            "Sets the UPnP device host to manual. Only needed when sharing media to a TV or console, which starts it on demand.",
            "upnphost", ServiceStartupType.Manual, backup, weight: 1)
        { Risk = RiskLevel.Advanced });

        // ---------------- Privacy ----------------

        tweaks.Add(new RegistryTweak("priv-telemetry", "Set telemetry to minimum",
            "AllowTelemetry=0 policy. On Pro+ this is 'Security level' (minimal); on Home it reduces to the required-data minimum. Stacks with disabling DiagTrack.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-adid", "Disable advertising ID",
            "Stops apps from using a unique ID to build an ad profile of you.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-activity", "Disable activity history & timeline upload",
            "Turns off activity feed collection and Microsoft-account upload of your app usage timeline.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, 1),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, 1),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-suggestions", "Disable suggestions & silent app installs",
            "No 'suggested content' in Start/Settings/lock screen, no tips notifications, and no auto-installing 'recommended' apps from the Store.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-copilot", "Disable Copilot",
            "Removes the Copilot taskbar entry and disables the built-in Copilot experience via policy. Doesn't uninstall the Copilot app - use Debloat for that.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, 0),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-recall", "Disable Recall (AI snapshotting)",
            "DisableAIDataAnalysis=1 policy: turns off the Recall feature that periodically screenshots your activity on Copilot+ PCs.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, 0)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-input", "Disable inking & typing personalization",
            "Stops Windows from sending what you type and write to Microsoft to build a local personalization dictionary.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-feedback", "Disable feedback requests",
            "Stops Windows from asking you for feedback about features (Siuf: zero requests per period).",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", 0, 0)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-tailored", "Disable tailored experiences",
            "Stops Microsoft from using your diagnostic data to personalise tips, ads and recommendations shown in Windows.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-cloud-content", "Disable consumer features & app pushes",
            "DisableWindowsConsumerFeatures=1: no automatic installs of promoted Store apps, no 'suggested' content in the shell. The single most effective anti-bloat policy.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CloudContent", "DisableWindowsConsumerFeatures", 1, 0)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-sync-provider", "Disable OneDrive ads in File Explorer",
            "Hides the 'Get more storage' / sync-provider banners that appear in Explorer's navigation pane.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-explorer-recent", "Hide recent & frequent folders",
            "Clears the 'Recent files' and 'Frequent folders' lists from Explorer's Home and Quick Access (ShowRecent=0, ShowFrequent=0, Start_TrackDocs=0).",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowRecent", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowFrequent", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-app-tracking", "Stop app-launch tracking",
            "Start_TrackProgs=0: Windows stops recording which programs you launch to 'improve' Start and search results.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-web-search", "Disable web results in Start search",
            "Start search becomes local-only: no Bing queries, no web suggestions, no cloud content. Noticeably faster and no keystrokes leave the PC.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0, 1),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1, 0),
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0, 1)
            })
        { Weight = 2, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-settings-suggestions", "Stop suggested content in Settings",
            "Removes the promotional cards and app suggestions Windows shows on the Settings home page.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338387Enabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-start-recommendations", "Clear the Start menu recommendations",
            "Turns off the suggested apps and account nudges in the Start menu. Your pinned apps and the app list are untouched.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_AccountNotifications", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-clipboard-sync", "Keep clipboard history on this PC",
            "Stops the clipboard from syncing to your Microsoft account and from uploading copied items. Local copy and paste keeps working.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Clipboard", "EnableClipboardHistory", 0, 1),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Clipboard", "CloudClipboardAutomaticUpload", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-edge-first-run", "Skip Edge first run and preload",
            "Hides the Edge welcome flow and stops the browser from staying in memory after you close it.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Edge", "HideFirstRunExperience", 1, 0),
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("priv-location-tracking", "Deny app access to your location",
            "Sets the system location consent to Deny. Apps that need it can still ask, and Maps still works while the setting is on.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow")
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        tweaks.Add(new RegistryTweak("priv-typing-insights", "Turn off typing insights",
            "Stops Windows from collecting a local sample of what you type to improve suggestions and autocorrect.",
            TweakCategory.Privacy, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Safe });

        // ---------------- Power ----------------

        tweaks.Add(new DelegateTweak(
            "power-ultimate", "Ultimate Performance power plan",
            "Activates the hidden Ultimate Performance plan (no aggressive CPU parking, no display sleep timers). Modern CPUs still downclock fine with it.",
            TweakCategory.Power,
            () => power.UltimateIsActive(settings),
            () => power.ActivateUltimate(settings) is not null
                ? TweakResult.Ok
                : TweakResult.Fail("powercfg could not create/activate the plan."),
            () => power.SetBalanced() ? TweakResult.Ok : TweakResult.Fail("Could not re-activate Balanced."))
        { Weight = 3, Risk = RiskLevel.Recommended });

        tweaks.Add(new DelegateTweak(
            "power-hibernation-off", "Disable hibernation",
            "Runs 'powercfg /h off': deletes hiberfil.sys (~40% of your RAM size) and disables Fast Startup. Frees disk; cold boots are unaffected.",
            TweakCategory.Power,
            // Registry, not "powercfg /a" text: that output is localized, so the English marker
            // never matched on a French or otherwise localized Windows.
            () => reg.ValueEquals(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", RegistryValueKind.DWord, 0),
            () => ProcessRunner.Run("powercfg.exe", "/h off").Success ? TweakResult.Ok : TweakResult.Fail("powercfg /h off failed."),
            () => ProcessRunner.Run("powercfg.exe", "/h on").Success ? TweakResult.Ok : TweakResult.Fail("powercfg /h on failed."))
        { Weight = 1, Risk = RiskLevel.Advanced });

        tweaks.Add(new RegistryTweak("power-fast-startup-off", "Disable Fast Startup",
            "HiberbootEnabled=0: a real shutdown instead of a hybrid hibernate. Fixes 'my settings reset' and dual-boot/update weirdness, and makes driver changes actually take effect on boot.",
            TweakCategory.Power, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Recommended });

        tweaks.Add(new RegistryTweak("power-throttling-off", "Disable power throttling (EcoQoS)",
            "PowerThrottlingOff=1: stops Windows from silently capping background processes to low-power cores/efficiency mode. Useful for encode, compile and stream workloads; costs a little battery.",
            TweakCategory.Power, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, 0)
            })
        { Weight = 2, Risk = RiskLevel.Advanced, RequiresReboot = true });

        tweaks.Add(new RegistryTweak("power-usb-selective-suspend-off", "Keep USB devices awake",
            "Stops Windows from suspending USB ports to save power. Fixes controllers, audio interfaces and external drives that drop out or need a replug.",
            TweakCategory.Power, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1, 0)
            })
        { Weight = 1, Risk = RiskLevel.Recommended, RequiresReboot = true });

        tweaks.Add(new RegistryTweak("power-modern-standby-off", "Use classic sleep instead of Modern Standby",
            "Forces the S3 sleep state on machines that support it. Classic sleep resumes faster and keeps network and audio devices working after wake.",
            TweakCategory.Power, reg,
            new[]
            {
                RegistryAction.SetDword(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "PlatformAoAcOverride", 0, 1)
            })
        { Weight = 1, Risk = RiskLevel.Advanced, RequiresReboot = true });

        tweaks.Add(new DelegateTweak("power-boost-mode", "Keep the CPU in boost mode",
            "Sets the processor performance boost mode to Aggressive for the active plan. Short bursts finish sooner, at the cost of a little heat.",
            TweakCategory.Power,
            () => ProcessRunner.Run("powercfg.exe", "/query SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE", 10_000)
                .StdOut.Contains("0x00000002", StringComparison.OrdinalIgnoreCase),
            () =>
            {
                ProcessRunner.Run("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 2", 10_000);
                var active = ProcessRunner.Run("powercfg.exe", "/setactive SCHEME_CURRENT", 10_000);
                return active.Success
                    ? TweakResult.Ok
                    : TweakResult.Fail("powercfg could not update the active plan.");
            },
            () =>
            {
                ProcessRunner.Run("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 0", 10_000);
                var active = ProcessRunner.Run("powercfg.exe", "/setactive SCHEME_CURRENT", 10_000);
                return active.Success
                    ? TweakResult.Ok
                    : TweakResult.Fail("powercfg could not update the active plan.");
            })
        { Weight = 2, Risk = RiskLevel.Advanced });

        return tweaks;
    }
}
