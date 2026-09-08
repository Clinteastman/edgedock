using System.Text.Json;

namespace EdgeDock;

internal sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeDock");
        Directory.CreateDirectory(root);
        _settingsPath = Path.Combine(root, "settings.json");
        WebViewProfilePath = Path.Combine(root, "WebView2");
    }

    public string WebViewProfilePath { get; }

    public async Task<string?> LoadUrlAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<StoredSettings>(stream);
            return IsAllowedUrl(settings?.DashboardUrl, out _) ? settings!.DashboardUrl : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveUrlAsync(string dashboardUrl)
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
                await JsonSerializer.SerializeAsync(stream, new StoredSettings(dashboardUrl));
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
            catch (IOException)
            {
                // A successful move removes the temporary file. Cleanup failure is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Saving already reports access errors; preserve the original exception.
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

    private sealed record StoredSettings(string DashboardUrl);
}
