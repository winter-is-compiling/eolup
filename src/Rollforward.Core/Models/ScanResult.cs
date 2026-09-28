namespace Rollforward.Core.Models;

public sealed record ScanResult(
    string ProjectPath,
    string CurrentVersion,
    string TargetVersion,
    string TargetDisplay,
    EolStatus Status,
    DateOnly? EolDate,
    int? DaysUntilEol,
    IReadOnlyList<string> UpgradePath,
    IReadOnlyList<string> Notes
);
