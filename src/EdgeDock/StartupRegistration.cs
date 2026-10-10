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
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "EdgeDock";

    /// <summary>True only when Windows will really start EdgeDock at sign-in.</summary>
    public static bool IsEnabled() => !string.IsNullOrWhiteSpace(StoredCommand()) && StartupApproval.AllowsRun(StoredApproval());

    /// <summary>True when the sign-in entry is enabled and starts this copy of EdgeDock.</summary>
    public static bool PointsHere() =>
        IsEnabled() && string.Equals(StoredCommand(), CurrentCommand, StringComparison.OrdinalIgnoreCase);

    private static byte[]? StoredApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            return key?.GetValue(ValueName) as byte[];
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string CurrentCommand => $"\"{Environment.ProcessPath}\" {StartupArgument}";

    private static string? StoredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
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
                key.SetValue(ValueName, CurrentCommand, RegistryValueKind.String);
                // Turning it on in EdgeDock is an explicit choice, so clear an earlier "disabled"
                // from Startup apps; a missing approval value means enabled.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
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
