namespace EdgeDock;

/// <summary>An app currently using the default output, as shown in the audio widget.</summary>
internal sealed record AudioSessionInfo(
    string Key,
    uint ProcessId,
    string Name,
    string? ExecutablePath,
    double VolumePercent,
    bool IsMuted,
    bool IsSystemSounds,
    bool IsActive);

/// <summary>Naming and ordering rules for app volume rows, kept free of COM for testing.</summary>
internal static class AudioNaming
{
    /// <summary>
    /// Apps rarely set a session display name, so fall back to the program's file description
    /// or process name. Windows' own entries are named plainly.
    /// </summary>
    public static string DisplayName(string? sessionName, string? fileDescription, string? processName, bool isSystemSounds)
    {
        if (isSystemSounds) return "System sounds";
        // Session names can be resource references such as "@%SystemRoot%\...dll,-202".
        if (!string.IsNullOrWhiteSpace(sessionName) && !sessionName.TrimStart().StartsWith('@')) return sessionName.Trim();
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var name = processName.Trim();
            return name.Length == 0 ? "App" : char.ToUpperInvariant(name[0]) + name[1..];
        }
        return "App";
    }

    /// <summary>Row key: one per process (browsers open several sessions), plus System sounds.</summary>
    public static string KeyFor(uint processId, bool isSystemSounds) => isSystemSounds ? "system" : $"pid:{processId}";

    /// <summary>
    /// One row per key, represented by its playing session if any. Playing apps first, then
    /// the rest alphabetically, with System sounds last.
    /// </summary>
    public static IReadOnlyList<AudioSessionInfo> Arrange(IEnumerable<AudioSessionInfo> sessions) =>
        sessions
            .GroupBy(session => session.Key)
            .Select(group => group.OrderByDescending(session => session.IsActive).First())
            .OrderBy(session => session.IsSystemSounds)
            .ThenByDescending(session => session.IsActive)
            .ThenBy(session => session.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
