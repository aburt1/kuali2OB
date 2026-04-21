namespace KualiOnBase.Api.Configuration;

public sealed class ImportOptions
{
    public const string SectionName = "Import";

    // Semicolon-separated list of absolute path prefixes that `targetFolderPath`
    // requests are allowed to land under. Fail-secure: empty list means every
    // request is rejected. Operators configure this once per deploy with the
    // real OnBase drop roots (`\\onbase-prod\DIP`, `/mnt/onbase-drop`, …) and
    // Kuali workflow authors can then only land documents inside those roots —
    // not `/etc`, not another tenant's share, not a container-root mount.
    public string AllowedTargetRoots { get; set; } = string.Empty;

    public IReadOnlyList<string> ParseAllowedRoots() =>
        string.IsNullOrWhiteSpace(AllowedTargetRoots)
            ? Array.Empty<string>()
            : AllowedTargetRoots
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath)
                .ToArray();
}
