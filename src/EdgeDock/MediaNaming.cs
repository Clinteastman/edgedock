namespace EdgeDock;

/// <summary>A media app Windows knows about, for choosing which one the widget controls.</summary>
internal sealed record MediaSource(string Id, string Name, bool IsPlaying, bool IsSelected);

/// <summary>Readable names for media sessions, kept free of Windows types for testing.</summary>
internal static class MediaNaming
{
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["spotify"] = "Spotify",
        ["vlc"] = "VLC",
        ["foobar2000"] = "foobar2000",
        ["musicbee"] = "MusicBee",
        ["itunes"] = "iTunes",
        ["discord"] = "Discord",
        ["plex"] = "Plex",
        ["tidal"] = "TIDAL"
    };

    /// <summary>
    /// Fallback when Windows cannot name the app. Session IDs are either a desktop executable
    /// (<c>chrome.exe</c>, sometimes a full path) or a packaged app ID
    /// (<c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>).
    /// </summary>
    public static string FallbackName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "Media app";
        var id = appId.Trim();
        string candidate;
        var bang = id.IndexOf('!');
        if (bang >= 0)
        {
            var entry = id[(bang + 1)..];
            // Generic entry points ("App") say nothing; use the package name instead.
            if (entry.Length > 0 && !string.Equals(entry, "App", StringComparison.OrdinalIgnoreCase)) candidate = entry;
            else
            {
                var family = id[..bang];
                var underscore = family.IndexOf('_');
                if (underscore > 0) family = family[..underscore];
                candidate = family.Contains('.') ? family[(family.LastIndexOf('.') + 1)..] : family;
            }
        }
        else
        {
            candidate = Path.GetFileName(id.Replace('/', '\\'));
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) candidate = candidate[..^4];
        }
        if (string.IsNullOrWhiteSpace(candidate)) return "Media app";
        if (KnownApps.TryGetValue(candidate, out var known)) return known;
        return char.ToUpperInvariant(candidate[0]) + candidate[1..];
    }
}
