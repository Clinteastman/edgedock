namespace EdgeDock;

/// <summary>
/// Timing rules for keeping always-on web cards alive. Kept free of WinUI types so the
/// console checks can cover them.
/// </summary>
internal static class WebRecovery
{
    /// <summary>Shorter sleeps rarely break a page; longer ones usually leave dashboards disconnected.</summary>
    public static readonly TimeSpan ResumeReloadThreshold = TimeSpan.FromSeconds(60);

    /// <summary>Gives Wi-Fi or Ethernet a moment to reconnect after waking before reloading.</summary>
    public static readonly TimeSpan ResumeReloadDelay = TimeSpan.FromSeconds(5);

    /// <summary>5, 10, 20, 40, then every 60 seconds.</summary>
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Clamp(attempt, 0, 4))));

    /// <summary>Reloads when the sleep was long, or when the start of the sleep was missed.</summary>
    public static bool ShouldReloadAfterResume(DateTimeOffset? suspendedAt, DateTimeOffset resumedAt) =>
        suspendedAt is null || resumedAt - suspendedAt.Value >= ResumeReloadThreshold;
}

/// <summary>
/// Allows a limited number of automatic recoveries in a rolling window, so a page that
/// crashes on every load stops reloading itself and asks the user instead.
/// </summary>
internal sealed class RecoveryBudget(int limit, TimeSpan window)
{
    private readonly Queue<DateTimeOffset> _uses = new();

    public bool TryConsume(DateTimeOffset now)
    {
        while (_uses.Count > 0 && now - _uses.Peek() >= window) _uses.Dequeue();
        if (_uses.Count >= limit) return false;
        _uses.Enqueue(now);
        return true;
    }
}
