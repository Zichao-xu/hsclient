using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace hsclient;

static class Program
{
    // ★ static!局部变量会被 GC,互斥/事件失效(M2.3 坑)
    private static Mutex? _singleton;
    private static EventWaitHandle? _showEvent;

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // —— M2.1:UAC 提权检测放最前;自重启透传原始命令行参数(否则 /tray 丢失) ——
        if (!IsRunningAsAdmin())
        {
            RelaunchAsAdmin();
            return;
        }

        // —— M2.3:单实例 Mutex + 命名事件唤起 ——
        _singleton = new Mutex(true, @"Global\hsclient_singleton", out bool createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\hsclient_show");

        if (!createdNew)
        {
            _showEvent.Set();   // 通知已存在的实例:把窗口亮出来(隐藏在托盘也可靠)
            return;
        }

        ApplicationConfiguration.Initialize();
        var form = new Form1();
        form.RegisterShowSignal(_showEvent);   // 把事件交给窗体起后台等待线程
        Application.Run(form);

        GC.KeepAlive(_singleton);
        GC.KeepAlive(_showEvent);
    }

    internal static bool IsRunningAsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RelaunchAsAdmin()
    {
        var psi = new ProcessStartInfo
        {
            FileName        = Environment.ProcessPath,  // .NET6+;single-file 安全,别用 Assembly.Location
            UseShellExecute = true,                     // ★ runas 必须 true(.NET8 默认 false)
            Verb            = "runas",                  // 触发 UAC
            // ★ 透传原始命令行参数:否则提权后 /tray 丢失,首启会闪主窗口
            Arguments       = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
        };
        try { Process.Start(psi); return true; }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED,用户点了"否"
        {
            MessageBox.Show("专网客户端需要管理员权限才能连接。", "需要权限",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
