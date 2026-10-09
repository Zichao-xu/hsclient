namespace hsclient;

internal interface IVpnEngine
{
    Task<string?> ReadBackendStateAsync(CancellationToken ct);
    Task EnsureServiceAsync(CancellationToken ct);
    Task ConnectAsync(string server, string key, CancellationToken ct);
    Task DisconnectAsync();
    Task StopServiceAsync();
}

/// <summary>All connection changes share one gate. A stop cancels queued and in-flight reconnects.</summary>
internal sealed class VpnSession
{
    private readonly IVpnEngine _engine;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource _onlineCts = new();
    private bool _wantsOnline;
    private bool _isExiting;
    private bool _observedInitialState;

    public VpnSession(IVpnEngine engine) => _engine = engine;
    public bool WantsOnline { get { lock (_stateGate) return _wantsOnline; } }
    public bool IsExiting { get { lock (_stateGate) return _isExiting; } }

    public void ObserveInitialState(string backend)
    {
        lock (_stateGate)
        {
            if (_observedInitialState) return;
            // Startup can briefly report Starting/NoState/unreachable before Running.
            // Do not lose the existing connection's intent during that transition.
            if (backend is not ("Running" or "Stopped")) return;
            _observedInitialState = true;
            _wantsOnline = backend == "Running" && !_isExiting;
        }
    }

    public async Task ConnectAsync(string server, string key)
    {
        CancellationTokenSource request;
        lock (_stateGate)
        {
            if (_isExiting) throw new InvalidOperationException("The client is exiting.");
            _observedInitialState = true;
            _onlineCts.Cancel();
            request = _onlineCts = new CancellationTokenSource();
            _wantsOnline = true;
        }
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(request.Token);
            entered = true;
            await _engine.EnsureServiceAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            await _engine.ConnectAsync(server, key, request.Token);
            request.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(request, _onlineCts)) _wantsOnline = false;
            }
            throw;
        }
        finally { if (entered) _operationGate.Release(); }
    }

    public async Task RecoverAsync(string server, string key, TimeSpan delay, bool reauthenticate)
    {
        CancellationToken ct;
        lock (_stateGate)
        {
            if (!_wantsOnline || _isExiting) return;
            ct = _onlineCts.Token;
        }
        await Task.Delay(delay, ct);
        await _operationGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await _engine.EnsureServiceAsync(ct);
            if (reauthenticate)
            {
                ct.ThrowIfCancellationRequested();
                await _engine.ConnectAsync(server, key, ct);
            }
        }
        finally { _operationGate.Release(); }
    }

    public Task DisconnectAsync() => StopAsync(exit: false);
    public Task ExitAsync() => StopAsync(exit: true);

    private async Task StopAsync(bool exit)
    {
        lock (_stateGate)
        {
            _wantsOnline = false;
            _observedInitialState = true;
            _isExiting |= exit;
            _onlineCts.Cancel();
        }
        await _operationGate.WaitAsync();
        try
        {
            if (!exit) { await _engine.DisconnectAsync(); return; }
            // Stopping the daemon removes the tunnel even if down failed.
            // A failed service stop is propagated; the GUI stays open for retry.
            try { await _engine.DisconnectAsync(); }
            catch (Exception) { }
            await _engine.StopServiceAsync();
        }
        finally { _operationGate.Release(); }
    }
}
