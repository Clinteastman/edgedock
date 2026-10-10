namespace EdgeDock;

internal enum LauncherTargetKind { Invalid, WebPage, File, Folder }

/// <summary>
/// What a launcher tile may open: an http/https page, or an existing file or folder by full
/// path. Nothing else — no command lines, arguments or relative paths — so a tile can only do
/// what double-clicking that item in Explorer would do.
/// </summary>
internal static class LauncherTargets
{
    /// <summary>Checks shape only, so a saved tile survives while its drive is unplugged.</summary>
    public static bool IsWellFormed(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        target = target.Trim();
        if (SettingsStore.IsAllowedUrl(target, out _)) return true;
        if (target.Contains('"') || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        return Path.IsPathFullyQualified(target) && !target.StartsWith(@"\\?\", StringComparison.Ordinal);
    }

    public static LauncherTargetKind Classify(string? target, Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        if (!IsWellFormed(target)) return LauncherTargetKind.Invalid;
        var trimmed = target!.Trim();
        if (SettingsStore.IsAllowedUrl(trimmed, out _)) return LauncherTargetKind.WebPage;
        if (folderExists(trimmed)) return LauncherTargetKind.Folder;
        if (fileExists(trimmed)) return LauncherTargetKind.File;
        return LauncherTargetKind.Invalid;
    }

    public static LauncherTargetKind Classify(string? target) => Classify(target, File.Exists, Directory.Exists);

    /// <summary>
    /// A readable default name: the web site's host, a folder's full name (dots included, as in
    /// <c>Projects.v2</c>), or a file's name without its extension. Pass the kind when known;
    /// unknown targets are treated as files, which only matters for dotted folder names.
    /// </summary>
    public static string DefaultName(string target, LauncherTargetKind kind = LauncherTargetKind.Invalid)
    {
        target = target.Trim();
        if (SettingsStore.IsAllowedUrl(target, out var uri) && uri is not null) return uri.Host;
        var trimmed = Path.TrimEndingDirectorySeparator(target);
        var fullName = Path.GetFileName(trimmed);
        var name = kind == LauncherTargetKind.Folder ? fullName : Path.GetFileNameWithoutExtension(trimmed);
        // ".config" has no name before its dot; fall back to the whole final part, then the path.
        if (string.IsNullOrWhiteSpace(name)) name = fullName;
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
