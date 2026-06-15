using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace hsclient;

public partial class Form1 : Form
{
    private readonly CancellationTokenSource _pollCts = new();
    private readonly ReconnectWatchdog _watchdog = new();

    // 用户意图标志:连接成功置 true、主动 down 置 false。看门狗据此决定要不要拉起。
    private volatile bool _userWantsOnline = false;

    // 唯一 UI 状态枚举:仅给托盘/状态灯着色用
    private enum LinkState { DaemonDown, Stopped, Starting, Online }

    // 轮询一次的完整结果:UI 用 Ui,看门狗用 BackendRaw + DaemonReachable(一份解析,不重复写)
    private readonly record struct PollResult(LinkState Ui, string BackendRaw, bool DaemonReachable);

    public Form1()
    {
        InitializeComponent();
        InitTray();
        // 回填已配置的控制服务器地址(未配置则显示占位默认值,提示用户改成真实地址)
        serverTextBox.Text = ServerStore.Load();
    }

    // ===================== M2.3:单实例唤起信号 =====================

    private EventWaitHandle? _showEvent;
    private Thread? _showWaiter;

    // 由 Program.Main 调用:起一个后台线程等"唤起"信号(替代不可靠的 HWND_BROADCAST 广播)
    public void RegisterShowSignal(EventWaitHandle ev)
    {
        _showEvent = ev;
        _showWaiter = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { break; }
                if (IsDisposed) break;
                try { BeginInvoke(new Action(ShowMainWindow)); } catch { break; }
            }
        }) { IsBackground = true };
        _showWaiter.Start();
    }

    // ===================== 生命周期 =====================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 首启最小化(配合 M3 计划任务开机自启):带 /tray 不闪主窗口
        if (Environment.GetCommandLineArgs().Skip(1)
                .Any(a => a.Equals("/tray", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = FormWindowState.Minimized;
            Hide();
            ShowInTaskbar = false;
        }

        _ = PollLoopAsync(_pollCts.Token);   // fire-and-forget,但 token 可控
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) { Hide(); ShowInTaskbar = false; }
    }

    // ★ 唯一的 OnFormClosing:点 X → 缩托盘;只有真退出才停轮询
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); ShowInTaskbar = false;   // 点 X → 缩托盘,不退出
            return;
        }
        _pollCts.Cancel();        // 真退出时才停轮询(关机/注销/任务管理器结束也走这里)
        base.OnFormClosing(e);
    }

    // ===================== M2.2:状态轮询 + UI 更新 =====================

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        do
        {
            try
            {
                PollResult r = await RefreshOnceAsync(ct);   // 裸 await,保 UI 亲和性
                ApplyState(r.Ui);                            // 在 UI 线程跑(await 续接)
                _watchdog.OnPoll(r.BackendRaw, r.DaemonReachable, _userWantsOnline);
            }
            catch (OperationCanceledException) { break; }    // 取消=正常退出
            catch (Exception)
            {
                ApplyState(LinkState.DaemonDown);            // 单次失败只置离线,绝不杀循环
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task<PollResult> RefreshOnceAsync(CancellationToken ct)
    {
        var (exit, stdout, _) = await RunTailscaleAsync("status --json", TimeSpan.FromSeconds(8), ct);
        // backend 没起时 BackendState="NoState" 但 exit 仍可能为 0 → 判定看字符串,不看退出码
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return new PollResult(LinkState.DaemonDown, "DaemonDown", false);
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            string backend = root.TryGetProperty("BackendState", out var b) ? (b.GetString() ?? "") : "";

            // ★ 在线判定只看 BackendState=="Running",不用 Self.Online 降级(B1)
            LinkState ui = backend switch
            {
                "Running"  => LinkState.Online,
                "Starting" => LinkState.Starting,
                "Stopped"  => LinkState.Stopped,
                _          => LinkState.DaemonDown,   // NoState/NeedsLogin/NeedsMachineAuth...
            };
            return new PollResult(ui, backend, true); // daemon 可达(status 解析成功)
        }
        catch (JsonException)
        {
            return new PollResult(LinkState.DaemonDown, "DaemonDown", false); // 解析失败兜底,不冒泡
        }
    }

    private void ApplyState(LinkState state)   // 只在 UI 线程被调用
    {
        switch (state)
        {
            case LinkState.Online:
                statusDot.BackColor = Color.LimeGreen; statusLabel.Text = "在线"; break;
            case LinkState.Starting:
                statusDot.BackColor = Color.Goldenrod; statusLabel.Text = "连接中"; break;
            case LinkState.Stopped:
                statusDot.BackColor = Color.Gray; statusLabel.Text = "已停止"; break;
            default:
                statusDot.BackColor = Color.Gray; statusLabel.Text = "离线"; break;
        }
        UpdateTrayState(state == LinkState.Online);   // 托盘图标/菜单联动
    }

    // ===================== M2.3:系统托盘 =====================

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _miConnect, _miDisconnect, _miShow, _miExit;
    private bool _reallyExit = false;

    private void InitTray()
    {
        _trayMenu = new ContextMenuStrip();
        _miShow       = new ToolStripMenuItem("显示主窗口", null, (s, e) => ShowMainWindow());
        _miConnect    = new ToolStripMenuItem("连接",       null, async (s, e) => await DoConnect());
        _miDisconnect = new ToolStripMenuItem("断开",       null, async (s, e) => await DoDisconnect());
        _miExit       = new ToolStripMenuItem("退出",       null, (s, e) => ExitApp());
        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            _miShow, new ToolStripSeparator(), _miConnect, _miDisconnect,
            new ToolStripSeparator(), _miExit
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.Offline,          // 启动默认离线灰
            Text = "hsclient — 未连接",        // ≤63 字符(NOTIFYICONDATA szTip 限制)
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

        Application.ApplicationExit += (s, e) =>   // 退出兜底,防图标残留
        {
            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
        };
    }

    private void ShowMainWindow()
    {
        Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal;
        Activate(); BringToFront();   // 抢焦点,缺一不可
    }

    private void ExitApp()
    {
        _reallyExit = true;
        if (_notifyIcon != null) _notifyIcon.Visible = false;  // 先灭图标
        Application.Exit();
    }

    // 由 ApplyState 调用
    public void UpdateTrayState(bool online)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Icon = online ? AppIcons.Online : AppIcons.Offline;
        _notifyIcon.Text = online ? "hsclient — 已连接" : "hsclient — 未连接"; // ≤63 字符
        if (_miConnect != null)    _miConnect.Enabled    = !online;
        if (_miDisconnect != null) _miDisconnect.Enabled = online;
    }

    // ===================== 连接 / 断开(M1 逻辑 + M2 接线) =====================

    private async void connectButton_Click(object sender, EventArgs e)
    {
        await DoConnect();
    }

    /// <summary>托盘"连接"与主窗"连接"按钮共用。无 key 时弹出主窗让用户填。</summary>
    private async Task DoConnect()
    {
        string server = serverTextBox.Text.Trim();
        if (string.IsNullOrEmpty(server) || server == ServerStore.DefaultServer)
        {
            ShowMainWindow();
            MessageBox.Show("请先填入控制服务器地址(服务器地址栏)。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string authKey = keyTextBox.Text.Trim();
        if (string.IsNullOrEmpty(authKey))
        {
            ShowMainWindow();
            MessageBox.Show("请先填入接入密钥。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        connectButton.Enabled = false;
        SetStatusText("连接中…");
        try
        {
            // ★ 首次手动连接必须带 --unattended(否则注销/重启掉登录)
            var (exitCode, _, stderr) = await RunTailscaleAsync(
                $"up --login-server={server} --authkey={authKey} --accept-routes --unattended --timeout=30s",
                TimeSpan.FromSeconds(40));

            if (exitCode != 0)
            {
                MessageBox.Show(FriendlyError(stderr), "连接失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 连接成功:记录用户意图 + 存 server/key 供看门狗复用
            _userWantsOnline = true;
            ServerStore.Save(server);
            AuthKeyStore.Save(authKey);
        }
        catch (OperationCanceledException)
        {
            // 连接超时 / 隧道太慢 / 本机缺少网络组件(如 wintun.dll 缺失导致虚拟网卡建不起来)。
            // 原始 .NET 文案是 "A task was canceled",对普通用户不友好,这里改成可读提示。
            MessageBox.Show(
                "连接超时,未能建立隧道。\r\n\r\n" +
                "可能原因:\r\n" +
                "  • 网络较慢或隧道暂时不通,可稍后重试;\r\n" +
                "  • 本机缺少必要的网络组件(虚拟网卡无法创建)。\r\n\r\n" +
                "如多次重试仍失败,请联系管理员。",
                "连接超时",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法调用 tailscale:{ex.Message}", "连接失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            connectButton.Enabled = true;
        }
        // 状态交给下一轮轮询刷新(4s 内),这里不重复解析
    }

    /// <summary>托盘"断开":tailscale down,并清用户意图(看门狗据此不再自动拉起)。</summary>
    private async Task DoDisconnect()
    {
        _userWantsOnline = false;   // 尊重用户主动断开
        try
        {
            await RunTailscaleAsync("down", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"断开失败:{ex.Message}", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetStatusText(string text) => statusLabel.Text = text;

    /// <summary>把 tailscale up 的 stderr 翻译成友好提示。</summary>
    private static string FriendlyError(string stderr)
    {
        string s = stderr?.ToLowerInvariant() ?? string.Empty;
        if (s.Contains("authkey") || s.Contains("auth key") ||
            s.Contains("invalid key") || s.Contains("expired") ||
            s.Contains("unauthorized"))
        {
            return "密钥无效或已过期,请向管理员重新获取。";
        }
        return "连接失败,请稍后重试或联系管理员。";
    }

    // ===================== tailscale CLI 调用(超时 + 取消 + Kill 防僵尸) =====================

    // ★ internal(非 private):ReconnectWatchdog 跨类调用(B2)
    internal static async Task<(int ExitCode, string StdOut, string StdErr)>
        RunTailscaleAsync(string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = ResolveTailscalePath(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();  // 两流并读防管道死锁
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            await proc.WaitForExitAsync();   // 等内核回收句柄,防泄漏
            throw;                           // 上层当一次失败(置离线)
        }
        return (proc.ExitCode, await outTask, await errTask);
    }

    // 落地路径:与 GUI 同目录的 tailscale.exe(不假设 PATH)。
    // 开发期同目录无 exe 时回退到 PATH 的 tailscale,方便自动化验证。
    private static string ResolveTailscalePath()
    {
        string sameDir = Path.Combine(AppContext.BaseDirectory, "tailscale.exe");
        return File.Exists(sameDir) ? sameDir : "tailscale";
    }
}
