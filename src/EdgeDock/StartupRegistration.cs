using Microsoft.Win32;

namespace EdgeDock;

/// <summary>
/// Starts EdgeDock when the current user signs in, using the per-user Run key.
/// No admin rights are needed, and Windows' Startup apps page can still turn it off.
/// </summary>
internal static class StartupRegistration
{
    public const string StartupArgument = "--startup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EdgeDock";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Returns false if Windows refused the change.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                // Always point at the copy being configured, so moving or rebuilding EdgeDock
                // and saving again repairs a stale path.
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {StartupArgument}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
