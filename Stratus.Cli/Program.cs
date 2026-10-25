using Stratus.Core;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;

namespace Stratus.Cli;

/// <summary>
/// Headless access to the engine: stratus list | score | info | apply &lt;id&gt; | revert &lt;id&gt;
/// Run from an elevated terminal for HKLM/service changes.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var backup = new BackupStore(AppPaths.BackupFile);
        var settings = new SettingsStore(AppPaths.SettingsFile);
        var registry = new RegistryService(backup);
        var power = new PowerPlanService();
        var engine = new TweakEngine(TweakCatalog.Build(registry, power, settings, backup));

        Console.WriteLine($"Stratus CLI — elevated: {SystemInfoService.IsElevated()}");
        Console.WriteLine();

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: stratus [list|score|info|apply <id>|revert <id>]");
            return 0;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list":
                foreach (var t in engine.Tweaks)
                {
                    var applied = SafeIsApplied(t);
                    var applicable = SafeIsApplicable(t);
                    Console.WriteLine($"{(applied ? "[x]" : "[ ]")} {(applicable ? "" : "(n/a) ")}{t.Id,-24} {t.Category,-9} {t.Risk,-12} w{t.Weight}  {t.Name}");
                }
                return 0;

            case "score":
                var report = engine.Rescore();
                Console.WriteLine($"Score: {report.Score}/100 ({report.StatusText}) — {report.AppliedCount}/{report.TotalCount} tweaks, weight {report.AppliedWeight}/{report.TotalWeight}");
                foreach (var c in report.Categories)
                    Console.WriteLine($"  {c.Category,-9} {c.Score,3}  ({c.AppliedCount}/{c.TotalCount})");
                return 0;

            case "info":
                Console.WriteLine($"CPU:  {SystemInfoService.GetCpu()}");
                Console.WriteLine($"GPU:  {SystemInfoService.GetGpu()}");
                Console.WriteLine($"RAM:  {SystemInfoService.GetRam()}");
                Console.WriteLine($"OS:   {SystemInfoService.GetOsDisplayName()} ({SystemInfoService.GetOsBuild()})");
                var plan = power.GetActivePlan();
                Console.WriteLine($"Plan: {plan?.Name ?? "unknown"}");
                return 0;

            case "apply":
            case "revert":
                if (args.Length < 2)
                {
                    Console.WriteLine($"Missing tweak id. Example: stratus {args[0]} game-dvr-off");
                    return 1;
                }
                var tweak = engine.Find(args[1]);
                if (tweak is null)
                {
                    Console.WriteLine($"Unknown tweak id '{args[1]}'. Run 'stratus list'.");
                    return 1;
                }
                var result = args[0].ToLowerInvariant() == "apply"
                    ? engine.ApplyAsync(tweak).GetAwaiter().GetResult()
                    : engine.RevertAsync(tweak).GetAwaiter().GetResult();
                Console.WriteLine(result.Success
                    ? $"{args[0]} OK: {tweak.Name}" + (tweak.RequiresReboot ? " (reboot required)" : "")
                    : $"{args[0]} FAILED: {result.Error}");
                return result.Success ? 0 : 1;

            default:
                Console.WriteLine($"Unknown command '{args[0]}'.");
                return 1;
        }
    }

    private static bool SafeIsApplied(Tweak t) { try { return t.IsApplied; } catch { return false; } }
    private static bool SafeIsApplicable(Tweak t) { try { return t.IsApplicable; } catch { return false; } }
}
