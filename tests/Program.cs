using System.Reflection;
using System.Diagnostics;
using System.ServiceProcess;
using hsclient;

internal static class Tests
{
    private static int _passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static async Task Throws<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task Run(string name, Func<Task> test)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"PASS {name}");
        _passed++;
    }

    private static async Task CoreTests()
    {
        await Run("exit disconnects before stopping the service", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); engine.Calls.Clear();
            await session.ExitAsync();
            Check(engine.Calls.SequenceEqual(new[] { "down", "stop" }), "Exit order");
            Check(engine.ServiceStopped && !session.WantsOnline, "Service must stop");
        });
        await Run("exit stops the service even if down fails", async () =>
        {
            var engine = new FakeEngine { FailDown = true }; var session = new VpnSession(engine);
            await session.ExitAsync(); Check(engine.ServiceStopped, "Must still stop");
        });
        await Run("failed service stop is reported and can be retried", async () =>
        {
            var engine = new FakeEngine { FailStop = true }; var session = new VpnSession(engine);
            await Throws<InvalidOperationException>(session.ExitAsync());
            Check(!engine.ServiceStopped && session.IsExiting, "Do not claim success");
            engine.FailStop = false; await session.ExitAsync(); Check(engine.ServiceStopped, "Retry failed");
        });
        await Run("manual disconnect retains the service and disables recovery", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); engine.Calls.Clear();
            await session.DisconnectAsync();
            Check(engine.Calls.SequenceEqual(new[] { "down" }) && !session.WantsOnline, "Disconnect behavior");
        });
        await Run("disconnect cancels a delayed reauthentication", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); engine.Calls.Clear();
            Task recovery = session.RecoverAsync("server", "key", TimeSpan.FromSeconds(60), true);
            await session.DisconnectAsync(); await Throws<OperationCanceledException>(recovery);
            Check(engine.Calls.SequenceEqual(new[] { "down" }), "Late up after down");
        });
        await Run("exit cancels in-flight connect before down and stop", async () =>
        {
            var engine = new FakeEngine { BlockConnect = true }; var session = new VpnSession(engine);
            Task connecting = session.ConnectAsync("server", "key");
            await engine.ConnectEntered.Task;
            await session.ExitAsync(); await Throws<OperationCanceledException>(connecting);
            Check(engine.Calls.SequenceEqual(new[] { "start", "up", "up-cancelled", "down", "stop" }), "Race ordering");
        });
        await Run("exit cancels in-flight recovery before stopping", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); engine.Calls.Clear();
            engine.BlockStart = true;
            Task recovery = session.RecoverAsync("", "", TimeSpan.Zero, false);
            await engine.StartEntered.Task;
            await session.ExitAsync(); await Throws<OperationCanceledException>(recovery);
            Check(engine.Calls.SequenceEqual(new[] { "start", "start-cancelled", "down", "stop" }), "Restart race");
        });
        await Run("watchdog cannot restart after manual disconnect", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); await session.DisconnectAsync(); engine.Calls.Clear();
            var watchdog = new ReconnectWatchdog(session, () => ("server", "key"), _ => TimeSpan.Zero);
            watchdog.OnPoll("DaemonDown", false); watchdog.OnPoll("NeedsLogin", true);
            Check(engine.Calls.Count == 0, "Unrequested restart");
        });
        await Run("watchdog deduplicates recovery and exit cancels it", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ConnectAsync("server", "key"); engine.Calls.Clear(); engine.BlockStart = true;
            var watchdog = new ReconnectWatchdog(session, () => ("server", "key"), _ => TimeSpan.Zero);
            watchdog.OnPoll("DaemonDown", false); await engine.StartEntered.Task;
            for (int i = 0; i < 20; i++) watchdog.OnPoll("DaemonDown", false);
            await session.ExitAsync(); watchdog.OnPoll("DaemonDown", false);
            Check(engine.Calls.Count(x => x == "start") == 1 && engine.ServiceStopped, "Recovery storm");
        });
        await Run("already running connection is adopted at startup", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            var watchdog = new ReconnectWatchdog(session, () => ("server", "key"), _ => TimeSpan.Zero);
            watchdog.OnPoll("Running", true); watchdog.OnPoll("DaemonDown", false);
            Check(session.WantsOnline && engine.Calls.SequenceEqual(new[] { "start" }), "Lost startup intent");
            await session.ExitAsync();
        });
        await Run("offline startup does not automatically start a service", () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            new ReconnectWatchdog(session, () => ("server", "key")).OnPoll("DaemonDown", false);
            Check(!session.WantsOnline && engine.Calls.Count == 0, "Offline startup restarted service");
            return Task.CompletedTask;
        });
        await Run("slow daemon startup retains the existing connection intent", () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            var watchdog = new ReconnectWatchdog(session, () => ("server", "key"), _ => TimeSpan.Zero);
            watchdog.OnPoll("Starting", true); watchdog.OnPoll("Running", true); watchdog.OnPoll("DaemonDown", false);
            Check(session.WantsOnline && engine.Calls.SequenceEqual(new[] { "start" }), "Lost intent during startup");
            return Task.CompletedTask;
        });
        await Run("a fresh application starts the service on explicit connect", async () =>
        {
            var engine = new FakeEngine(); await new VpnSession(engine).ExitAsync(); engine.Calls.Clear();
            await new VpnSession(engine).ConnectAsync("server", "key");
            Check(!engine.ServiceStopped && engine.Calls.SequenceEqual(new[] { "start", "up" }), "Reconnect after exit");
        });
        await Run("failed connect clears online intent", async () =>
        {
            var engine = new FakeEngine { FailConnect = true }; var session = new VpnSession(engine);
            await Throws<InvalidOperationException>(session.ConnectAsync("server", "key"));
            Check(!session.WantsOnline, "Failed connect leaves recovery enabled");
        });
        await Run("no new connection is accepted after exit", async () =>
        {
            var engine = new FakeEngine(); var session = new VpnSession(engine);
            await session.ExitAsync(); engine.Calls.Clear();
            await Throws<InvalidOperationException>(session.ConnectAsync("server", "key"));
            Check(engine.Calls.Count == 0, "Started after exit");
        });
        await Run("native Windows service stops on exit and restarts on next connect", NativeServiceTest);
        await Run("exit succeeds when the service is not installed", async () =>
        {
            var engine = new TailscaleEngine($"hsclient-absent-{Environment.ProcessId}");
            await new VpnSession(engine).ExitAsync();
        });
    }

    private static async Task<int> Sc(params string[] arguments)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private static async Task NativeServiceTest()
    {
        string name = $"hsclient-test-{Environment.ProcessId}";
        string binPath = $"\"{Environment.ProcessPath}\" --service {name}";
        Check(await Sc("create", name, "binPath=", binPath, "start=", "demand") == 0, "Cannot create isolated test service");
        try
        {
            string backend = "Stopped";
            var calls = new List<string>();
            var engine = new TailscaleEngine(name, (arguments, _, _) =>
            {
                string command = arguments.First(); calls.Add(command);
                if (command == "up") backend = "Running";
                if (command == "down") backend = "Stopped";
                return Task.FromResult((0, $"{{\"BackendState\":\"{backend}\"}}", ""));
            });
            using var service = new ServiceController(name);
            var first = new VpnSession(engine);
            await first.ConnectAsync("https://test.example", "test-key");
            service.Refresh(); Check(service.Status == ServiceControllerStatus.Running, "Native service did not start");
            await first.ExitAsync(); service.Refresh();
            Check(service.Status == ServiceControllerStatus.Stopped && backend == "Stopped", "Native exit did not stop service");
            var second = new VpnSession(engine);
            await second.ConnectAsync("https://test.example", "test-key");
            service.Refresh(); Check(service.Status == ServiceControllerStatus.Running, "Reopen did not restart service");
            await second.ExitAsync(); service.Refresh();
            Check(service.Status == ServiceControllerStatus.Stopped, "Second exit failed");
            Check(calls.SequenceEqual(new[] { "up", "down", "status", "up", "down", "status" }), "Native backend sequence");
        }
        finally
        {
            await Sc("stop", name);
            Check(await Sc("delete", name) == 0, "Test service cleanup failed");
        }
    }

    private static Task Invoke(Form1 form, string method) => (Task)typeof(Form1)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
    private static T Field<T>(Form1 form, string name) => (T)typeof(Form1)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static async Task WaitFor(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(7);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("UI state did not update");
            await Task.Delay(30);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--service")
        {
            ServiceBase.Run(new TestService(args[1]));
            return 0;
        }
        try
        {
            CoreTests().GetAwaiter().GetResult();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var engine = new FakeEngine { Backend = "Stopped", ServiceStopped = true };
            var settings = new FakeSettings();
            using var form = new Form1(engine, settings);
            Exception? uiFailure = null;
            form.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(100);
                    var button = Field<Button>(form, "connectButton");
                    var label = Field<Label>(form, "statusLabel");
                    Check(button.Text == "连接", "Initial button");
                    button.PerformClick();
                    await WaitFor(() => button.Text == "断开" && label.Text == "在线");
                    Check(settings.SaveCount == 1, "Settings not saved");
                    if (args.Length > 0)
                    {
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(args[0], "client-online.png"));
                    }
                    button.PerformClick();
                    await WaitFor(() => label.Text == "已停止");
                    Check(!engine.ServiceStopped, "Disconnect must retain service");
                    button.PerformClick();
                    await WaitFor(() => button.Text == "断开");
                    form.Close(); // X must hide to tray, not disconnect.
                    Check(!form.IsDisposed && !form.Visible && engine.Backend == "Running", "X changed connection");
                    Field<ToolStripMenuItem>(form, "_miShow").PerformClick();
                    Check(form.Visible, "Tray restore");
                    Field<ToolStripMenuItem>(form, "_miExit").PerformClick();
                }
                catch (Exception ex) { uiFailure = ex; Application.Exit(); }
            };
            Application.Run(form);
            if (uiFailure != null) throw uiFailure;
            Check(engine.ServiceStopped && engine.Backend == "Stopped", "GUI exited without cleanup");
            Check(engine.Calls.TakeLast(2).SequenceEqual(new[] { "down", "stop" }), "GUI exit ordering");
            Console.WriteLine("PASS Windows UI: connect, disconnect, reconnect, X-to-tray, restore, exit");
            Console.WriteLine($"RESULT {_passed + 1}/{_passed + 1} passed (isolated backend; real VPN unchanged)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

internal sealed class TestService : ServiceBase
{
    public TestService(string name) { ServiceName = name; AutoLog = false; }
}

internal sealed class FakeSettings : IClientSettings
{
    public int SaveCount;
    public string LoadServer() => "https://test.example";
    public string LoadKey() => "test-key";
    public void Save(string server, string key) => SaveCount++;
}

internal sealed class FakeEngine : IVpnEngine
{
    public List<string> Calls { get; } = new();
    public string Backend = "Running";
    public bool ServiceStopped, FailDown, FailStop, FailConnect, BlockConnect, BlockStart;
    public TaskCompletionSource ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<string?> ReadBackendStateAsync(CancellationToken ct) => Task.FromResult<string?>(Backend);
    public async Task EnsureServiceAsync(CancellationToken ct)
    {
        Calls.Add("start");
        if (BlockStart)
        {
            StartEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Calls.Add("start-cancelled"); throw; }
        }
        ServiceStopped = false;
    }
    public async Task ConnectAsync(string server, string key, CancellationToken ct)
    {
        Calls.Add("up");
        if (FailConnect) throw new InvalidOperationException("Test failure");
        if (BlockConnect)
        {
            ConnectEntered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Calls.Add("up-cancelled"); throw; }
        }
        Backend = "Running";
    }
    public Task DisconnectAsync()
    {
        Calls.Add("down");
        if (FailDown) throw new InvalidOperationException("Test failure");
        Backend = "Stopped";
        return Task.CompletedTask;
    }
    public Task StopServiceAsync()
    {
        Calls.Add("stop");
        if (FailStop) throw new InvalidOperationException("Test failure");
        ServiceStopped = true; Backend = "Stopped";
        return Task.CompletedTask;
    }
}
