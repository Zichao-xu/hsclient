namespace hsclient;

/// <summary>One cancellable recovery at a time; a manual stop always wins.</summary>
internal sealed class ReconnectWatchdog
{
    private readonly VpnSession _session;
    private readonly Func<(string Server, string Key)> _loadCredentials;
    private readonly Func<int, TimeSpan> _backoff;
    private Task _work = Task.CompletedTask;
    private int _failStreak;

    public ReconnectWatchdog(VpnSession session, Func<(string Server, string Key)> loadCredentials,
        Func<int, TimeSpan>? backoff = null)
    {
        _session = session;
        _loadCredentials = loadCredentials;
        _backoff = backoff ?? (failures => TimeSpan.FromSeconds(
            new[] { 5, 10, 30, 60 }[Math.Min(failures, 3)] + Random.Shared.Next(0, 3)));
    }

    // Called only by the UI poll loop. Recovery itself never touches the UI.
    public void OnPoll(string backendRaw, bool daemonReachable)
    {
        _session.ObserveInitialState(backendRaw);
        if (!_session.WantsOnline || _session.IsExiting || !_work.IsCompleted) return;
        if (backendRaw == "Running") { _failStreak = 0; return; }
        bool reauth = daemonReachable && backendRaw is "NeedsLogin" or "NeedsMachineAuth";
        if (daemonReachable && !reauth) return;
        _work = RecoverAsync(reauth);
    }

    private async Task RecoverAsync(bool reauth)
    {
        try
        {
            var (server, key) = reauth ? _loadCredentials() : ("", "");
            if (reauth && (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(server))) return;
            await _session.RecoverAsync(server, key, _backoff(_failStreak), reauth);
            _failStreak = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _failStreak = Math.Min(_failStreak + 1, 3); }
    }
}
