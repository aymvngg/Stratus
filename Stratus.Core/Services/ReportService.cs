using System.Text;
using Stratus.Core.Models;

namespace Stratus.Core.Services;

/// <summary>
/// Writes a plain markdown system report: score, hardware, per-category breakdown and
/// every tweak with its live state. Useful for support threads and for keeping a
/// before/after record. Files land in ProgramData\Stratus\reports.
/// </summary>
public static class ReportService
{
    public static string Create(ScoreReport report, SystemSummary summary)
    {
        Directory.CreateDirectory(AppPaths.ReportsDir);
        var path = Path.Combine(AppPaths.ReportsDir, $"stratus-report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, Render(report, summary, SystemInfoService.RebootPending(), DateTime.Now), Encoding.UTF8);
        Log.Info($"System report written to {path}");
        return path;
    }

    internal static string Render(ScoreReport report, SystemSummary summary, bool rebootPending, DateTime generatedAt)
    {
        var applied = report.States.Where(s => s.IsApplicable && s.IsApplied).ToList();
        var pending = report.States
            .Where(s => s.IsApplicable && !s.IsApplied && s.ReadError is null && s.Tweak.Risk <= RiskLevel.Recommended)
            .ToList();
        var notApplicable = report.States.Count(s => !s.IsApplicable && s.ReadError is null);
        var unknown = report.States.Where(s => s.ReadError is not null).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Stratus system report");
        sb.AppendLine();
        sb.AppendLine($"Generated {generatedAt:yyyy-MM-dd HH:mm} on this machine.");
        sb.AppendLine();
        sb.AppendLine("## Score");
        sb.AppendLine();
        sb.AppendLine($"- Score: **{report.ScoreText}/100** ({report.StatusText})");
        sb.AppendLine($"- Weight earned: {report.AppliedWeight} of {report.TotalWeight}");
        sb.AppendLine($"- Tweaks applied: {report.AppliedCount} of {report.TotalCount} applicable");
        sb.AppendLine($"- Not applicable on this machine: {notApplicable}");
        sb.AppendLine($"- Restart pending: {(rebootPending ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("## System");
        sb.AppendLine();
        sb.AppendLine($"- CPU: {summary.Cpu}");
        sb.AppendLine($"- GPU: {summary.Gpu}");
        sb.AppendLine($"- Memory: {summary.Ram}");
        sb.AppendLine($"- OS: {summary.Os} ({summary.OsBuild})");
        sb.AppendLine($"- Running as administrator: {(summary.IsElevated ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("## By category");
        sb.AppendLine();
        sb.AppendLine("| Category | Score | Applied |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var category in report.Categories.Where(c => c.TotalCount > 0))
            sb.AppendLine($"| {category.Category} | {category.Score} | {category.AppliedCount}/{category.TotalCount} |");

        sb.AppendLine();
        sb.AppendLine("## Applied tweaks");
        sb.AppendLine();
        if (applied.Count == 0)
            sb.AppendLine("None.");
        foreach (var state in applied)
            sb.AppendLine($"- {state.Tweak.Name} ({state.Tweak.Category}, {state.Tweak.Risk})");

        sb.AppendLine();
        sb.AppendLine("## Pending safe and recommended tweaks");
        sb.AppendLine();
        if (pending.Count == 0)
            sb.AppendLine(unknown.Count > 0
                ? "None of the tweaks that could be read. Some states could not be read, so this may not be complete."
                : "None. Every safe and recommended tweak is applied.");
        foreach (var state in pending)
            sb.AppendLine($"- {state.Tweak.Name} ({state.Tweak.Category}, {state.Tweak.Risk})");

        if (unknown.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Could not read");
            sb.AppendLine();
            foreach (var state in unknown)
                sb.AppendLine($"- {state.Tweak.Name} ({state.Tweak.Category}): {state.ReadError}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Reversible tweaks use saved originals when available. Cleanup deletions cannot be undone.");

        return sb.ToString();
    }
}
