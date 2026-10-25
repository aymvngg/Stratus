namespace Stratus.Core.Models;

public sealed record SystemSummary(
    string Cpu,
    string Gpu,
    string Ram,
    string Os,
    string OsBuild,
    bool IsElevated);
