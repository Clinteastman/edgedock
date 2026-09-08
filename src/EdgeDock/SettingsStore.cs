using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeDock;

internal enum MediaPanelPlacement
{
    Right,
    Left,
    Hidden
}

internal sealed record EdgeDockSettings(
    string? DashboardUrl,
    MediaPanelPlacement MediaPanelPlacement,
    MediaPanelPlacement LastVisibleMediaPanelPlacement,
    double MediaPanelWidth,
    bool ShowArtwork);

internal sealed class SettingsStore
{
    private readonly string _settingsPath;
    private Dictionary<string, JsonElement>? _extensionData;

    public SettingsStore()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("EDGEDOCK_DATA_DIR");
        var root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdgeDock")
            : Path.GetFullPath(overrideRoot);
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
        WebViewProfilePath = Path.Combine(root, "WebView2");
    }

    public string WebViewProfilePath { get; }

    public async Task<EdgeDockSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return Defaults();
            }

            await using var stream = File.OpenRead(_settingsPath);
            var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(stream);
            _extensionData = stored?.ExtensionData;
            var url = IsAllowedUrl(stored?.DashboardUrl, out _) ? stored!.DashboardUrl : null;
            var placement = ParsePlacement(stored?.MediaPanelPlacement, MediaPanelPlacement.Right, true);
            var lastVisible = ParsePlacement(stored?.LastVisibleMediaPanelPlacement, MediaPanelPlacement.Right, false);
            var width = Math.Clamp(stored?.MediaPanelWidth ?? 340, 240, 440);
            return new(url, placement, lastVisible, width, stored?.ShowArtwork ?? true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults();
        }
    }

    public async Task SaveAsync(EdgeDockSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new StoredSettings
                {
                    DashboardUrl = settings.DashboardUrl,
                    MediaPanelPlacement = settings.MediaPanelPlacement.ToString(),
                    LastVisibleMediaPanelPlacement = settings.LastVisibleMediaPanelPlacement.ToString(),
                    MediaPanelWidth = Math.Clamp(settings.MediaPanelWidth, 240, 440),
                    ShowArtwork = settings.ShowArtwork,
                    ExtensionData = _extensionData
                });
                await stream.FlushAsync();
            }

            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the save exception; a leftover temporary file is harmless.
            }
        }
    }

    public static bool IsAllowedUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static MediaPanelPlacement ParsePlacement(string? value, MediaPanelPlacement fallback, bool allowHidden)
    {
        return Enum.TryParse<MediaPanelPlacement>(value, true, out var parsed) &&
               Enum.IsDefined(parsed) &&
               (allowHidden || parsed != MediaPanelPlacement.Hidden)
            ? parsed
            : fallback;
    }

    private static EdgeDockSettings Defaults() =>
        new(null, MediaPanelPlacement.Right, MediaPanelPlacement.Right, 340, true);

    private sealed class StoredSettings
    {
        public string? DashboardUrl { get; set; }
        public string? MediaPanelPlacement { get; set; }
        public string? LastVisibleMediaPanelPlacement { get; set; }
        public double? MediaPanelWidth { get; set; }
        public bool? ShowArtwork { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}
