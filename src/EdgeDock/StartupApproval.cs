namespace EdgeDock;

/// <summary>
/// Windows' Startup apps page and Task Manager do not delete a Run entry when the user turns
/// it off; they record the choice under Explorer\StartupApproved\Run. The first byte of that
/// value is even when enabled (0x02, 0x06) and odd when disabled (0x03, 0x07). A missing
/// value means the entry has never been switched, so it runs.
/// </summary>
internal static class StartupApproval
{
    public static bool AllowsRun(byte[]? approval) => approval is not { Length: > 0 } || (approval[0] & 1) == 0;
}
