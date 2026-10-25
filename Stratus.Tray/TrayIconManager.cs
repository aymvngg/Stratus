using System.Runtime.InteropServices;

namespace Stratus.Tray;

/// <summary>
/// Notification-area presence for the Stratus app. WinForms NotifyIcon lives in its own library
/// because UseWindowsForms cannot coexist with WinUI XAML compilation in one project.
/// The owner supplies UI-thread callbacks; all events are raised on the creating thread.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly Action _showWindow;
    private readonly Action _requestExit;
    private readonly Action<string>? _log;
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Bitmap? _bitmap;
    private readonly IntPtr _iconHandle;
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconManager(Action showWindow, Action requestExit, Action<string>? log = null)
    {
        _showWindow = showWindow;
        _requestExit = requestExit;
        _log = log;

        var handle = IntPtr.Zero;
        System.Drawing.Bitmap? bitmap = null;
        try
        {
            bitmap = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedRect(0.5f, 0.5f, 31f, 31f, 8f);
                using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x0F, 0x6C, 0xBD));
                g.FillPath(fill, path);
                using var font = new System.Drawing.Font("Segoe UI", 15f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                var size = g.MeasureString("S", font);
                g.DrawString("S", font, System.Drawing.Brushes.White, (32f - size.Width) / 2f, (32f - size.Height) / 2f);
            }
            handle = bitmap.GetHicon();
        }
        catch
        {
            bitmap?.Dispose();
            bitmap = null;
        }

        _bitmap = bitmap;
        _iconHandle = handle;

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Stratus",
            Icon = handle != IntPtr.Zero
                ? System.Drawing.Icon.FromHandle(handle)
                : System.Drawing.SystemIcons.Application,
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Stratus", null, (_, _) => _showWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _log?.Invoke("Exit requested from the tray menu.");
            _requestExit();
        });
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                _showWindow();
        };
    }

    /// <summary>Tray tooltip doubles as a score glance: "Stratus: 84/100".</summary>
    public void UpdateTooltip(int score)
    {
        var text = $"Stratus: {score}/100";
        if (_icon.Text != text)
            _icon.Text = text;
    }

    /// <summary>One-time hint so hiding to the tray never looks like the app vanished.</summary>
    public void OnHiddenToTray()
    {
        if (_balloonShown)
            return;
        _balloonShown = true;
        _icon.ShowBalloonTip(2500, "Stratus keeps running",
            "It stays in the notification area and keeps tracking your score. Click the icon to reopen.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _bitmap?.Dispose();
        if (_iconHandle != IntPtr.Zero)
            DestroyIcon(_iconHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
