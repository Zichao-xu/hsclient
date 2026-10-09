using System.Diagnostics;
using System.Text;

namespace hsclient;

public partial class Form1 : Form
{
    private readonly CancellationTokenSource _pollCts = new();
    private readonly VpnSession _session;
    private readonly IVpnEngine _engine;
    private readonly IClientSettings _settings;
    private readonly ReconnectWatchdog _watchdog;
    private Task _pollTask = Task.CompletedTask;
    private bool _operationBusy;
    private bool _exitInProgress;

    // 唯一 UI 状态枚举:仅给托盘/状态灯着色用
    private enum LinkState { DaemonDown, Stopped, Starting, Online }
    private LinkState _lastUiState = LinkState.DaemonDown;

    // 轮询一次的完整结果:UI 用 Ui,看门狗用 BackendRaw + DaemonReachable(一份解析,不重复写)
    private readonly record struct PollResult(LinkState Ui, string BackendRaw, bool DaemonReachable);

    public Form1() : this(new TailscaleEngine(), new ClientSettings()) { }

    internal Form1(IVpnEngine engine, IClientSettings settings)
    {
        _engine = engine;
        _settings = settings;
        _session = new VpnSession(engine);
        _watchdog = new ReconnectWatchdog(_session, () =>
        {
            string server = _settings.LoadServer();
            return server == ServerStore.DefaultServer ? ("", "") : (server, _settings.LoadKey());
        });
        InitializeComponent();
        InitTray();
        // 回填已配置的控制服务器地址(未配置则显示占位默认值,提示用户改成真实地址)
        serverTextBox.Text = _settings.LoadServer();
        keyTextBox.Text = _settings.LoadKey();
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

        _pollTask = PollLoopAsync(_pollCts.Token);
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
        if (!_reallyExit && e.CloseReason == CloseReason.ApplicationExitCall)
        {
            e.Cancel = true;
            _ = ExitAppAsync();
            return;
        }
        _pollCts.Cancel();        // 真退出时才停轮询(关机/注销/任务管理器结束也走这里)
        base.OnFormClosing(e);
    }

    // ===================== M2.2:状态轮询 + UI 更新 =====================

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            do
            {
                try
                {
                    PollResult r = await RefreshOnceAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    if (!_operationBusy) ApplyState(r.Ui);
                    _watchdog.OnPoll(r.BackendRaw, r.DaemonReachable);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { if (!ct.IsCancellationRequested) ApplyState(LinkState.DaemonDown); }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task<PollResult> RefreshOnceAsync(CancellationToken ct)
    {
        string? backend = await _engine.ReadBackendStateAsync(ct);
        if (backend == null)
            return new PollResult(LinkState.DaemonDown, "DaemonDown", false);
        LinkState ui = backend switch
        {
            "Running" => LinkState.Online,
            "Starting" => LinkState.Starting,
            "Stopped" => LinkState.Stopped,
            _ => LinkState.DaemonDown,
        };
        return new PollResult(ui, backend, true);
    }

    private void ApplyState(LinkState state)   // 只在 UI 线程被调用
    {
        _lastUiState = state;
        connectButton.Text = state == LinkState.Online ? "断开" : "连接";
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
        _miExit       = new ToolStripMenuItem("退出并断开", null, async (s, e) => await ExitAppAsync());
        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            _miShow, new ToolStripSeparator(), _miConnect, _miDisconnect,
            new ToolStripSeparator(), _miExit
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcons.Offline,          // 启动默认离线灰
            Text = "专网客户端 — 未连接",
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

    private async Task ExitAppAsync()
    {
        if (_exitInProgress || _reallyExit) return;
        _exitInProgress = true;
        _pollCts.Cancel();
        SetStatusText("正在断开并退出…");
        SetOperationControls();
        try
        {
            await _session.ExitAsync();
            await _pollTask;
            _reallyExit = true;
            if (_notifyIcon != null) _notifyIcon.Visible = false;
            Application.Exit();
        }
        catch (Exception)
        {
            // Keep the application visible: never claim it is disconnected on a failed stop.
            ShowMainWindow();
            SetStatusText("退出失败，连接可能仍在运行");
            MessageBox.Show("未能停止专网服务，连接可能仍在运行。请重试“退出并断开”，或联系管理员。",
                "退出未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _exitInProgress = false;
            if (!IsDisposed) SetOperationControls();
        }
    }

    private void SetOperationControls()
    {
        bool canOperate = !_operationBusy && !_exitInProgress && !_session.IsExiting;
        connectButton.Enabled = canOperate;
        serverTextBox.Enabled = canOperate;
        keyTextBox.Enabled = canOperate;
        if (_miConnect != null) _miConnect.Enabled = canOperate && _lastUiState != LinkState.Online;
        if (_miDisconnect != null) _miDisconnect.Enabled = canOperate && _lastUiState == LinkState.Online;
        if (_miExit != null) _miExit.Enabled = !_exitInProgress;
    }

    // 由 ApplyState 调用
    public void UpdateTrayState(bool online)
    {
        if (_notifyIcon == null) return;
        _notifyIcon.Icon = online ? AppIcons.Online : AppIcons.Offline;
        _notifyIcon.Text = online ? "专网客户端 — 已连接" : "专网客户端 — 未连接";
        bool canOperate = !_operationBusy && !_exitInProgress && !_session.IsExiting;
        if (_miConnect != null)    _miConnect.Enabled    = canOperate && !online;
        if (_miDisconnect != null) _miDisconnect.Enabled = canOperate && online;
    }

    // ===================== 连接 / 断开(M1 逻辑 + M2 接线) =====================

    private async void connectButton_Click(object sender, EventArgs e)
    {
        if (_lastUiState == LinkState.Online) await DoDisconnect();
        else await DoConnect();
    }

    /// <summary>托盘"连接"与主窗"连接"按钮共用。无 key 时弹出主窗让用户填。</summary>
    private async Task DoConnect()
    {
        if (_operationBusy || _session.IsExiting) return;
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

        _operationBusy = true;
        SetOperationControls();
        SetStatusText("连接中…");
        try
        {
            // ★ 首次手动连接必须带 --unattended(否则注销/重启掉登录)
            await _session.ConnectAsync(server, authKey);

            // 连接成功:记录用户意图 + 存 server/key 供看门狗复用
            _settings.Save(server, authKey);
        }
        catch (TailscaleCommandException ex)
        {
            if (!_session.IsExiting)
                MessageBox.Show(FriendlyError(ex.ErrorOutput), "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) when (_session.IsExiting) { }
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
        catch (Exception)
        {
            if (!_session.IsExiting)
                MessageBox.Show("无法启动专网连接，请确认客户端已正确安装，或联系管理员。", "连接失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _operationBusy = false;
            if (!IsDisposed) SetOperationControls();
        }
        // 状态交给下一轮轮询刷新(4s 内),这里不重复解析
    }

    /// <summary>托盘"断开":tailscale down,并清用户意图(看门狗据此不再自动拉起)。</summary>
    private async Task DoDisconnect()
    {
        if (_operationBusy || _session.IsExiting) return;
        _operationBusy = true;
        SetOperationControls();
        SetStatusText("正在断开…");
        try
        {
            await _session.DisconnectAsync();
            if (!_session.IsExiting) ApplyState(LinkState.Stopped);
        }
        catch (Exception)
        {
            if (!_session.IsExiting)
                MessageBox.Show("未能确认专网已断开。请重试，或选择“退出并断开”。", "断开未完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _operationBusy = false; if (!IsDisposed) SetOperationControls(); }
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
        RunTailscaleAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = ResolveTailscalePath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);
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
