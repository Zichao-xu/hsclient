using System.Diagnostics;

namespace hsclient;

/// <summary>
/// 断线自动重连看门狗(混合方案,daemon 自愈为主、GUI 兜底)。
/// 只在 daemon 进死态(NeedsLogin/NeedsMachineAuth)时用已存 authkey 触发一次带退避的 up;
/// daemon 服务挂了则重启 Windows 服务。正常掉线/网络抖动交给 daemon 自愈,绝不重填 key(防把已认证节点踢下线)。
/// </summary>
internal sealed class ReconnectWatchdog
{
    private readonly object _upGate = new();        // 串行化:同时只允许一个 up(防风暴)
    private readonly object _svcGate = new();       // 串行化:同时只允许一个重启服务
    private int _failStreak = 0;
    private const string ServiceName = "Tailscale"; // 已用 sc query 实测核实:服务名为 Tailscale

    private TimeSpan Backoff()                       // 5s,10s,30s,60s 封顶 + 抖动
    {
        int[] steps = { 5, 10, 30, 60 };
        int s = steps[Math.Min(_failStreak, steps.Length - 1)];
        return TimeSpan.FromSeconds(s + Random.Shared.Next(0, 3));
    }

    // 由 PollLoopAsync 每轮调用;签名与 M2.2 调用处严格一致(string, bool, bool)
    public void OnPoll(string backendRaw, bool daemonReachable, bool userWantsOnline)
    {
        if (!daemonReachable) { _ = Task.Run(RestartServiceWithRetry); return; } // 服务挂→重启服务
        switch (backendRaw)
        {
            case "Running":   _failStreak = 0; return;        // 绿,清退避
            case "Starting":
            case "NoState":   return;                          // daemon 自连,不插手
            case "Stopped":   return;                          // 用户主动 down,不自动拉起
            case "NeedsLogin":
            case "NeedsMachineAuth":
                if (userWantsOnline) _ = Task.Run(TriggerReauth); // 唯一带 key 路径
                return;
        }
    }

    private async Task TriggerReauth()
    {
        if (!Monitor.TryEnter(_upGate)) return;   // 已有 up 在跑,跳过
        try
        {
            string key = AuthKeyStore.Load();      // 从 DPAPI 取
            if (string.IsNullOrEmpty(key)) return; // 没存过 key,无从重连(等用户手填)

            string server = ServerStore.Load();    // 取用户配置的控制服务器地址
            if (string.IsNullOrEmpty(server) || server == ServerStore.DefaultServer)
                return;                            // 未配置真实地址,无从重连(等用户手填)

            await Task.Delay(Backoff());           // 退避后再动手
            // ★ login-server 取自用户配置;★ 必须 --unattended(重启/注销后保持连接);★ --timeout 防永久阻塞
            var (exit, _, _) = await Form1.RunTailscaleAsync(
                $"up --login-server={server} --authkey={key} --accept-routes --unattended --timeout=30s",
                TimeSpan.FromSeconds(40));
            _failStreak = exit == 0 ? 0 : _failStreak + 1;
        }
        finally { Monitor.Exit(_upGate); }
    }

    // 重启 Windows 服务,带重试 + 退避
    private void RestartServiceWithRetry()
    {
        if (!Monitor.TryEnter(_svcGate)) return;  // 已有重启在跑,跳过(防风暴)
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (RunSc($"start {ServiceName}")) return;   // 起来了就收工
                // 起不来:确保停干净再起(stop 失败不致命)
                RunSc($"stop {ServiceName}");
                Thread.Sleep(TimeSpan.FromSeconds(3 + attempt * 5)); // 3s,8s,13s 退避
            }
        }
        finally { Monitor.Exit(_svcGate); }
    }

    private static bool RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe", Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;   // sc start 已运行时返回非 0(1056),交给下一轮 poll 复核即可
        }
        catch { return false; }
    }
}
