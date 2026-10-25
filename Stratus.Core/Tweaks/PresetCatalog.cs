using Stratus.Core.Models;

namespace Stratus.Core.Tweaks;

/// <summary>
/// Curated one-click sets. Every id must exist in <see cref="TweakCatalog"/>;
/// unknown or not-applicable ids are skipped at runtime, so presets stay safe on any machine.
/// </summary>
public static class PresetCatalog
{
    public static IReadOnlyList<Preset> All =>
    [
        Gaming, Balanced, MaxPrivacy,
    ];

    public static Preset Gaming { get; } = new(
        "gaming", "Gaming PC",
        "Latency and frame pacing first: background capture off, foreground priority, network throttling removed, performance visuals.",
        "\uE768",
        [
            "game-dvr-off", "gamebar-popups-off", "game-mode-on", "hags-on", "game-priority-separation",
            "game-mouse-accel-off", "transparency-off", "visualfx-performance", "menu-delay", "hover-time",
            "net-nagle-off", "net-throttling-off", "net-games-task", "net-system-responsiveness",
            "net-delivery-optimization", "power-ultimate", "power-fast-startup-off",
        ]);

    public static Preset Balanced { get; } = new(
        "balanced", "Balanced",
        "Everyday responsiveness and privacy basics. No visual downgrades, nothing controversial.",
        "\uE73E",
        [
            "game-dvr-off", "gamebar-popups-off", "game-mode-on", "menu-delay", "hover-time",
            "visual-aero-shake-off", "visual-widgets-off", "net-throttling-off", "net-system-responsiveness",
            "net-delivery-optimization", "svc-diagtrack", "svc-dmwappush", "svc-remoteregistry", "svc-wer", "svc-trx",
            "priv-telemetry", "priv-adid", "priv-activity", "priv-suggestions", "priv-feedback", "priv-tailored",
            "priv-cloud-content", "priv-sync-provider", "power-fast-startup-off",
        ]);

    public static Preset MaxPrivacy { get; } = new(
        "privacy", "Max Privacy",
        "Minimum data leaving the PC: telemetry, ads, tracking and cloud content off, plus the services that ship the data.",
        "\uE72E",
        [
            "priv-telemetry", "priv-adid", "priv-activity", "priv-suggestions", "priv-copilot", "priv-recall",
            "priv-input", "priv-feedback", "priv-tailored", "priv-cloud-content", "priv-sync-provider",
            "priv-explorer-recent", "priv-app-tracking", "priv-web-search",
            "svc-diagtrack", "svc-dmwappush", "svc-remoteregistry", "svc-wer", "svc-trx", "svc-wmpnet", "svc-phonesvc",
        ]);
}
