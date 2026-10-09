using System.ServiceProcess;
using System.ComponentModel;

namespace hsclient;

internal sealed class TailscaleCommandException : Exception
{
    public string ErrorOutput { get; }
    public TailscaleCommandException(string output) : base("The VPN command failed.") => ErrorOutput = output;
}

internal sealed class TailscaleEngine : IVpnEngine
{
    private readonly string _serviceName;
    private readonly Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> _run;

    public TailscaleEngine(string serviceName = "Tailscale",
        Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<(int, string, string)>>? run = null)
    {
        _serviceName = serviceName;
        _run = run ?? Form1.RunTailscaleAsync;
    }

    public async Task<string?> ReadBackendStateAsync(CancellationToken ct)
    {
        var (exit, output, _) = await _run(new[] { "status", "--json" }, TimeSpan.FromSeconds(8), ct);
        if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("BackendState", out var state) ? state.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public async Task EnsureServiceAsync(CancellationToken ct)
    {
        using var service = FindService() ?? throw new InvalidOperationException("The VPN service is not installed.");
        service.Refresh();
        if (service.Status == ServiceControllerStatus.StopPending)
            await WaitForAsync(service, ServiceControllerStatus.Stopped, ct);
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        await WaitForAsync(service, ServiceControllerStatus.Running, ct);
    }

    public async Task ConnectAsync(string server, string key, CancellationToken ct)
    {
        var (exit, _, error) = await _run(
            new[] { "up", $"--login-server={server}", $"--authkey={key}", "--accept-routes", "--unattended", "--timeout=30s" },
            TimeSpan.FromSeconds(40), ct);
        if (exit != 0) throw new TailscaleCommandException(error);
    }

    public async Task DisconnectAsync()
    {
        using var service = FindService();
        if (service == null) return;
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        var (exit, _, error) = await _run(new[] { "down" }, TimeSpan.FromSeconds(20), CancellationToken.None);
        if (exit != 0) throw new TailscaleCommandException(error);
        if (await ReadBackendStateAsync(CancellationToken.None) != "Stopped")
            throw new InvalidOperationException("The VPN has not disconnected.");
    }

    public async Task StopServiceAsync()
    {
        using var service = FindService();
        if (service == null) return;
        service.Refresh();
        if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
            service.Stop();
        await WaitForAsync(service, ServiceControllerStatus.Stopped, CancellationToken.None);
    }

    private static async Task WaitForAsync(ServiceController service, ServiceControllerStatus target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == target) return;
            await Task.Delay(200, timeout.Token);
        }
    }

    private ServiceController? FindService()
    {
        var service = new ServiceController(_serviceName);
        try { service.Refresh(); _ = service.Status; return service; }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        { service.Dispose(); return null; }
        catch { service.Dispose(); throw; }
    }
}
