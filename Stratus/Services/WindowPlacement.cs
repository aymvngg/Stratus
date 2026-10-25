using System.Runtime.InteropServices;
using Stratus.ViewModels;

namespace Stratus.Services;

/// <summary>
/// Persists the main window bounds in settings.json so Stratus reopens where you
/// left it. Values are clamped to the current virtual desktop, so unplugging a
/// monitor can never leave the window stranded off-screen.
/// </summary>
public static class WindowPlacement
{
    private const string KeyX = "winX";
    private const string KeyY = "winY";
    private const string KeyW = "winW";
    private const string KeyH = "winH";
    private const string KeyMax = "winMax";

    private const int MinWidth = 960;
    private const int MinHeight = 640;

    public sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>SM_XVIRTUALSCREEN and friends, in pixels.</summary>
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public static Placement? Load()
    {
        var settings = AppServices.Settings;
        if (!int.TryParse(settings.Get(KeyX), out var x) ||
            !int.TryParse(settings.Get(KeyY), out var y) ||
            !int.TryParse(settings.Get(KeyW), out var w) ||
            !int.TryParse(settings.Get(KeyH), out var h))
        {
            return null;
        }

        var vx = GetSystemMetrics(SmXVirtualScreen);
        var vy = GetSystemMetrics(SmYVirtualScreen);
        var vw = Math.Max(MinWidth, GetSystemMetrics(SmCxVirtualScreen));
        var vh = Math.Max(MinHeight, GetSystemMetrics(SmCyVirtualScreen));

        w = Math.Clamp(w, MinWidth, vw);
        h = Math.Clamp(h, MinHeight, vh);
        x = Math.Clamp(x, vx, vx + vw - w);
        y = Math.Clamp(y, vy, vy + vh - h);

        return new Placement(x, y, w, h, settings.Get(KeyMax) == "true");
    }

    public static void Save(Placement placement)
    {
        var settings = AppServices.Settings;
        settings.Set(KeyX, placement.X.ToString());
        settings.Set(KeyY, placement.Y.ToString());
        settings.Set(KeyW, placement.Width.ToString());
        settings.Set(KeyH, placement.Height.ToString());
        settings.Set(KeyMax, placement.Maximized ? "true" : "false");
    }
}
