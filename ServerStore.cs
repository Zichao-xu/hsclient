namespace hsclient;

/// <summary>
/// 控制服务器地址持久化。地址非机密,明文存到 ProgramData\hsclient\server.txt
/// (与 authkey 同目录,SYSTEM 服务与管理员 GUI 都可读)。
/// 未配置时回退到占位默认值(非真实端点),仓库公开也不泄露任何真实地址;
/// 用户必须在 GUI 的"服务器地址"框填入真实控制服务器地址后才能连接。
/// </summary>
internal static class ServerStore
{
    // 占位默认值:绝不是真实端点。仅作回退/提示用,用户必须改成自己的控制服务器地址。
    public const string DefaultServer = "https://your-headscale-server.example";

    // 存到 ProgramData\hsclient(与 AuthKeyStore 同目录)
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "hsclient");
    private static readonly string FilePath = Path.Combine(Dir, "server.txt");

    public static void Save(string server)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, (server ?? string.Empty).Trim());
    }

    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return DefaultServer;
            string s = File.ReadAllText(FilePath).Trim();
            return string.IsNullOrEmpty(s) ? DefaultServer : s;
        }
        catch { return DefaultServer; }   // 读不到/损坏 → 回退占位默认值,等用户重填
    }
}
