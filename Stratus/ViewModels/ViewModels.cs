namespace Stratus.ViewModels;

/// <summary>Static singletons — pages cache, VMs survive navigation.</summary>
public static class Vm
{
    public static readonly DashboardViewModel Dashboard = new();
    public static readonly TweaksViewModel Tweaks = new();
    public static readonly DebloatViewModel Debloat = new();
    public static readonly CleanupViewModel Cleanup = new();
    public static readonly ToolsViewModel Tools = new();
}
