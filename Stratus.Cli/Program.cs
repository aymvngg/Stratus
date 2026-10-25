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
        var history = new HistoryStore(AppPaths.HistoryFile);
        var registry = new RegistryService(backup);
        var startup = new StartupService(registry);
        var scheduledTasks = new ScheduledTaskService(AppPaths.TasksFile);
        var power = new PowerPlanService();
        var engine = new TweakEngine(TweakCatalog.Build(registry, power, settings, backup), history);

        Console.WriteLine($"Stratus CLI - elevated: {SystemInfoService.IsElevated()}");
        Console.WriteLine();

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: stratus [list|score|info|presets|startup|tasks|history|backups|apply <id>|revert <id>]");
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
                Console.WriteLine($"Score: {report.Score}/100 ({report.StatusText}) - {report.AppliedCount}/{report.TotalCount} tweaks, weight {report.AppliedWeight}/{report.TotalWeight}");
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

            case "presets":
                foreach (var preset in PresetCatalog.All)
                {
                    var total = 0;
                    var applied = 0;
                    foreach (var id in preset.TweakIds)
                    {
                        var t = engine.Find(id);
                        if (t is null || !SafeIsApplicable(t))
                            continue;
                        total++;
                        if (SafeIsApplied(t))
                            applied++;
                    }
                    Console.WriteLine($"{preset.Name,-12} {applied}/{total} applied{(applied == total && total > 0 ? "  (complete)" : "")}");
                    Console.WriteLine($"  {preset.Description}");
                }
                return 0;

            case "startup":
                var startupItems = startup.Scan();
                if (startupItems.Count == 0)
                {
                    Console.WriteLine("No startup entries found.");
                    return 0;
                }
                foreach (var item in startupItems)
                {
                    var state = item.IsEnabled ? "[on ]" : "[off]";
                    var changed = startup.IsModified(item) ? " *" : "  ";
                    Console.WriteLine($"{state}{changed} {item.Name,-32} {item.Scope,-13} {item.Source}");
                    var command = item.Command;
                    if (command.Length > 100)
                        command = command[..97] + "...";
                    Console.WriteLine($"          {command}");
                }
                Console.WriteLine();
                Console.WriteLine($"{startupItems.Count(i => i.IsEnabled)} of {startupItems.Count} enabled. * = changed by Stratus");
                return 0;

            case "tasks":
                var taskItems = scheduledTasks.Scan();
                if (taskItems.Count == 0)
                {
                    Console.WriteLine("No known scheduled tasks found.");
                    return 0;
                }
                foreach (var item in taskItems)
                {
                    var state = item.IsEnabled ? "[on ]" : "[off]";
                    var changed = scheduledTasks.IsModified(item) ? " *" : "  ";
                    Console.WriteLine($"{state}{changed} {item.Name,-48} {item.Group}");
                }
                Console.WriteLine();
                Console.WriteLine($"{taskItems.Count(i => !i.IsEnabled)} of {taskItems.Count} disabled. * = changed by Stratus");
                return 0;

            case "history":
                var entries = history.All();
                if (entries.Count == 0)
                {
                    Console.WriteLine("No changes recorded yet.");
                    return 0;
                }
                foreach (var e in entries.Take(20))
                {
                    var status = e.Success ? "ok" : "FAILED: " + (e.Error ?? "unknown");
                    Console.WriteLine($"{e.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Action,-9} {e.TweakName,-38} {status}");
                }
                if (entries.Count > 20)
                    Console.WriteLine($"... {entries.Count - 20} older entries (open the app to see all)");
                return 0;

            case "backups":
                var all = backup.All();
                if (all.Count == 0)
                {
                    Console.WriteLine("No backups stored yet.");
                    return 0;
                }
                foreach (var group in all.GroupBy(e => e.TweakId).OrderBy(g => g.Key))
                {
                    var name = engine.Find(group.Key)?.Name ?? group.Key;
                    var values = group.ToList();
                    Console.WriteLine($"{name}  ({group.Key}, {values.Count} value{(values.Count == 1 ? "" : "s")})");
                    foreach (var v in values)
                    {
                        var target = v.ScopeType == "service"
                            ? "service " + v.ScopeId
                            : v.ScopeId.Replace("|", "\\");
                        var old = v.Existed ? (v.ValueJson ?? "") : "(did not exist)";
                        if (old.Length > 60)
                            old = old[..57] + "...";
                        Console.WriteLine($"  {target} = {old}");
                    }
                }
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
