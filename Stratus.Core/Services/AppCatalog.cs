namespace Stratus.Core.Services;

/// <summary>
/// One app that Stratus can install through winget. Id is the winget package identifier,
/// so the same value is shown, searched and passed to the CLI.
/// </summary>
public sealed record AppEntry(
    string Id,
    string Name,
    string Description,
    string Category,
    bool Recommended = false);

/// <summary>
/// Curated list of apps worth having on a fresh Windows install. This is metadata only:
/// nothing is downloaded or run until the user picks an entry on the Install page.
/// </summary>
public static class AppCatalog
{
    public static IReadOnlyList<AppEntry> Apps { get; } =
    [
        // Browsers
        new("Mozilla.Firefox", "Firefox", "Independent browser with strong tracking protection.", "Browsers", true),
        new("Google.Chrome", "Chrome", "Google's browser and its extension ecosystem.", "Browsers"),
        new("Brave.Brave", "Brave", "Chromium browser with built-in ad and tracker blocking.", "Browsers", true),
        new("Opera.Opera", "Opera", "Chromium browser with a built-in VPN and sidebar.", "Browsers"),
        new("Vivaldi.Vivaldi", "Vivaldi", "Highly customizable Chromium browser.", "Browsers"),

        // Communication
        new("Discord.Discord", "Discord", "Voice, video and text chat for communities.", "Communication", true),
        new("Microsoft.Teams", "Microsoft Teams", "Meetings and chat for work and school.", "Communication"),
        new("Zoom.Zoom", "Zoom", "Video meetings and webinars.", "Communication"),
        new("SlackTechnologies.Slack", "Slack", "Team chat and channels.", "Communication"),
        new("Telegram.TelegramDesktop", "Telegram Desktop", "Messaging client for Telegram.", "Communication"),
        new("WhatsApp.WhatsApp", "WhatsApp", "WhatsApp desktop client.", "Communication"),

        // Development
        new("Microsoft.VisualStudioCode", "Visual Studio Code", "Code editor with extensions for most languages.", "Development", true),
        new("Git.Git", "Git", "Version control, including Git Bash.", "Development", true),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "JavaScript runtime for tools and web development.", "Development"),
        new("Python.Python.3.12", "Python 3.12", "Python interpreter and pip.", "Development"),
        new("Microsoft.PowerShell", "PowerShell 7", "Modern cross-platform PowerShell.", "Development", true),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Tabbed terminal for PowerShell, cmd and WSL.", "Development", true),
        new("GitHub.GitHubDesktop", "GitHub Desktop", "Graphical Git client for GitHub.", "Development"),
        new("Docker.DockerDesktop", "Docker Desktop", "Containers on Windows with WSL2.", "Development"),
        new("JetBrains.Toolbox", "JetBrains Toolbox", "Installs and updates JetBrains IDEs.", "Development"),
        new("Postman.Postman", "Postman", "API client for testing HTTP services.", "Development"),
        new("Microsoft.DotNet.SDK.8", ".NET 8 SDK", "Build and run .NET applications.", "Development"),

        // Media
        new("VideoLAN.VLC", "VLC media player", "Plays almost every video and audio format.", "Media", true),
        new("Spotify.Spotify", "Spotify", "Music and podcast streaming.", "Media", true),
        new("OBSProject.OBSStudio", "OBS Studio", "Screen recording and live streaming.", "Media"),
        new("HandBrake.HandBrake", "HandBrake", "Video transcoder for smaller files.", "Media"),
        new("GIMP.GIMP", "GIMP", "Image editor for photos and graphics.", "Media"),
        new("Audacity.Audacity", "Audacity", "Audio recorder and editor.", "Media"),
        new("KDE.Krita", "Krita", "Digital painting and illustration.", "Media"),
        new("BlenderFoundation.Blender", "Blender", "3D modeling, animation and rendering.", "Media"),

        // Productivity
        new("Microsoft.PowerToys", "PowerToys", "Microsoft utilities: FancyZones, PowerRename, Run and more.", "Productivity", true),
        new("Obsidian.Obsidian", "Obsidian", "Local Markdown notes with links and plugins.", "Productivity"),
        new("Notion.Notion", "Notion", "Notes, docs and project boards.", "Productivity"),
        new("LibreOffice.LibreOffice", "LibreOffice", "Free office suite: Writer, Calc, Impress.", "Productivity", true),
        new("Bitwarden.Bitwarden", "Bitwarden", "Open source password manager.", "Productivity", true),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", "Offline password database.", "Productivity"),
        new("Adobe.Acrobat.Reader.64-bit", "Acrobat Reader", "Reads and signs PDF files.", "Productivity"),
        new("SumatraPDF.SumatraPDF", "SumatraPDF", "Fast, lightweight PDF and ebook reader.", "Productivity", true),
        new("ShareX.ShareX", "ShareX", "Screenshots, screen recording and uploads.", "Productivity", true),

        // Utilities
        new("7zip.7zip", "7-Zip", "Archives and extracts most compression formats.", "Utilities", true),
        new("voidtools.Everything", "Everything", "Instant file search across every drive.", "Utilities", true),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Reads drive health and SMART data.", "Utilities"),
        new("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", "Measures drive read and write speed.", "Utilities"),
        new("CPUID.CPU-Z", "CPU-Z", "Reads CPU, memory and motherboard details.", "Utilities"),
        new("TechPowerUp.GPU-Z", "GPU-Z", "Reads graphics card details and sensors.", "Utilities"),
        new("Microsoft.Sysinternals.Autoruns", "Autoruns", "Shows everything that starts with Windows.", "Utilities"),
        new("Wireshark.Wireshark", "Wireshark", "Network protocol analyzer.", "Utilities"),
        new("PuTTY.PuTTY", "PuTTY", "SSH and telnet client.", "Utilities"),
        new("WinSCP.WinSCP", "WinSCP", "SFTP and FTP client with a file manager.", "Utilities"),
        new("Rufus.Rufus", "Rufus", "Creates bootable USB drives.", "Utilities"),
        new("Ventoy.Ventoy", "Ventoy", "Boots ISO files straight from a USB drive.", "Utilities"),
        new("qBittorrent.qBittorrent", "qBittorrent", "BitTorrent client without ads.", "Utilities", true),
        new("Tailscale.Tailscale", "Tailscale", "Private mesh VPN between your devices.", "Utilities"),
        new("WireGuard.WireGuard", "WireGuard", "Fast, simple VPN tunnels.", "Utilities"),
        new("Proton.ProtonVPN", "Proton VPN", "Privacy-focused VPN client.", "Utilities"),
        new("RustDesk.RustDesk", "RustDesk", "Open source remote desktop.", "Utilities"),
        new("AnyDesk.AnyDesk", "AnyDesk", "Remote desktop access.", "Utilities"),
        new("TeamViewer.TeamViewer", "TeamViewer", "Remote support and meetings.", "Utilities"),
        new("AutoHotkey.AutoHotkey", "AutoHotkey", "Scripts keyboard shortcuts and automation.", "Utilities"),

        // Gaming
        new("Valve.Steam", "Steam", "Game store, library and friends.", "Gaming", true),
        new("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Epic store and free game library.", "Gaming"),
        new("GOG.Galaxy", "GOG Galaxy", "GOG store and library, plus other launchers.", "Gaming"),
        new("ElectronicArts.EADesktop", "EA app", "EA game library and store.", "Gaming"),
        new("Ubisoft.Connect", "Ubisoft Connect", "Ubisoft games and store.", "Gaming"),
        new("Playnite.Playnite", "Playnite", "One library for every game launcher.", "Gaming"),
        new("PrismLauncher.PrismLauncher", "Prism Launcher", "Minecraft launcher with mod support.", "Gaming"),
    ];
}
