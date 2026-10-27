using Stratus.Core;
using Stratus.Core.Models;
using Stratus.Core.Services;
using Stratus.Core.Tweaks;

namespace Stratus.Cli;

/// <summary>
/// Headless access to the engine: stratus list | score | info | presets | startup | tasks | cleanup | history | backups | docgen [--json] [path] | apply &lt;id&gt; | revert &lt;id&gt; | apply --preset &lt;id&gt; | revert --preset &lt;id&gt;
/// Run from an elevated terminal for HKLM/service changes.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Log.Init();
        Log.Info("CLI invoked: " + (args.Length == 0 ? "(no args)" : string.Join(' ', args)));

        var backup = new BackupStore(AppPaths.BackupFile);
        var settings = new SettingsStore(AppPaths.SettingsFile);
        var history = new HistoryStore(AppPaths.HistoryFile);
        var registry = new RegistryService(backup);
        var startup = new StartupService(registry);
        var scheduledTasks = new ScheduledTaskService(AppPaths.TasksFile);
        var cleanup = new CleanupService();
        var power = new PowerPlanService();
        var engine = new TweakEngine(TweakCatalog.Build(registry, power, settings, backup), history);

        Console.WriteLine($"Stratus CLI - elevated: {SystemInfoService.IsElevated()}");
        Console.WriteLine();

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: stratus [list|score|info|presets|startup|tasks|cleanup|history|backups|docgen [--json] [path]|apply <id>|revert <id>|apply --preset <id>|revert --preset <id>]");
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

            case "dns-bench":
                Console.WriteLine("Measuring resolver latency (3 queries each)...");
                foreach (var bench in DnsBenchmarkService.Run())
                    Console.WriteLine($"  {bench.Label,-24} {bench.Server,-16} {bench.LatencyText}");
                return 0;

            case "report":
                var reportPath = ReportService.Create(engine.Rescore(), new SystemSummary(
                    SystemInfoService.GetCpu(),
                    SystemInfoService.GetGpu(),
                    SystemInfoService.GetRam(),
                    SystemInfoService.GetOsDisplayName(),
                    SystemInfoService.GetOsBuild(),
                    SystemInfoService.IsElevated()));
                Console.WriteLine($"Report: {reportPath}");
                return 0;

            case "docgen":
                return RunDocgen(engine, args);

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

            case "cleanup":
                var totalBytes = 0L;
                foreach (var target in cleanup.Scan())
                {
                    var size = target.Exists ? FormatBytes(target.SizeBytes) : "not present";
                    Console.WriteLine($"{(target.Exists ? "[ok]" : "[--]")} {target.Name,-42} {size,10}  {target.FileCount} files");
                    totalBytes += target.SizeBytes;
                }
                Console.WriteLine();
                Console.WriteLine($"{FormatBytes(totalBytes)} reclaimable in total. Cleaning runs in the app.");
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
                if (string.Equals(args[1], "--preset", StringComparison.OrdinalIgnoreCase))
                    return RunPreset(engine, args[0].ToLowerInvariant() == "apply", args);
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

    /// <summary>
    /// Writes the reference docs from the compiled catalog. The catalog is the only source,
    /// so regenerating is always safe: nothing here reads the existing files.
    /// </summary>
    private static int RunDocgen(TweakEngine engine, string[] args)
    {
        var json = args.Length > 1 && string.Equals(args[1], "--json", StringComparison.OrdinalIgnoreCase);
        var requested = json
            ? (args.Length > 2 ? args[2] : null)
            : (args.Length > 1 ? args[1] : null);
        var target = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine("docs", json ? "catalog.json" : "reference.md")
            : requested;

        try
        {
            var fullPath = Path.GetFullPath(target);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = json
                ? DocGenerator.BuildJson(engine.Tweaks)
                : DocGenerator.BuildMarkdown(engine.Tweaks);
            File.WriteAllText(fullPath, text);

            Log.Info($"Docs written to {fullPath}");
            Console.WriteLine($"Wrote {engine.Tweaks.Count} tweaks to {fullPath}.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write docs to {target}: {ex.Message}");
            Console.WriteLine($"Could not write {target}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Applies or reverts a whole preset. Unknown, not-applicable and already-in-that-state
    /// tweaks are skipped; every attempted tweak prints its own result line.
    /// </summary>
    private static int RunPreset(TweakEngine engine, bool apply, string[] args)
    {
        var validIds = string.Join(", ", PresetCatalog.All.Select(p => p.Id));

        if (args.Length < 3)
        {
            Console.WriteLine($"Missing preset id. Valid ids: {validIds}");
            return 1;
        }

        var preset = PresetCatalog.All.FirstOrDefault(p =>
            string.Equals(p.Id, args[2], StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Name, args[2], StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            Console.WriteLine($"Unknown preset '{args[2]}'. Valid ids: {validIds}");
            return 1;
        }

        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // One bulk scope so the app rescores once at the end instead of per tweak.
        using var bulk = engine.BeginBulk();

        foreach (var id in preset.TweakIds)
        {
            if (!seen.Add(id))
                continue;

            var tweak = engine.Find(id);
            if (tweak is null || !SafeIsApplicable(tweak))
            {
                skipped++;
                continue;
            }

            var alreadyApplied = SafeIsApplied(tweak);
            if (apply ? alreadyApplied : !alreadyApplied)
            {
                skipped++;
                continue;
            }

            var result = apply
                ? engine.ApplyAsync(tweak).GetAwaiter().GetResult()
                : engine.RevertAsync(tweak).GetAwaiter().GetResult();

            if (result.Success)
            {
                succeeded++;
                Console.WriteLine($"  ok   {tweak.Id} - {tweak.Name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {tweak.Id} - {tweak.Name}: {result.Error ?? "unknown error"}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(apply
            ? $"Applied {succeeded} of {preset.TweakIds.Count} tweaks in preset '{preset.Name}'."
            : $"Reverted {succeeded} of {preset.TweakIds.Count} tweaks in preset '{preset.Name}'.");
        if (skipped > 0)
            Console.WriteLine($"Skipped {skipped}: unknown, not applicable on this PC, or already in that state.");

        return failed == 0 ? 0 : 1;
    }

    private static string FormatBytes(long bytes)
        => bytes <= 0 ? "0 B"
        : bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1} MB"
        : $"{bytes / 1024.0:F0} KB";
}
