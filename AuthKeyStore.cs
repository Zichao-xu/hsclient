using System.Security.Cryptography;
using System.Text;

namespace hsclient;

/// <summary>
/// authkey 持久化(看门狗复用 key 自动重连用)。
/// DPAPI ProtectedData + DataProtectionScope.LocalMachine(SYSTEM 服务与管理员 GUI 都要能读)+ 固定 entropy。
///
/// 安全边界(坦白):LocalMachine scope 加密的数据本机任意进程都能解密 —— 约等于"不显眼的明文",
/// 仅防肉眼直读配置文件,不防本机恶意进程。本项目(单机客户端、authkey 为可复用长效 key)接受此边界:
/// 可用性优先(用户 scope 会让 SYSTEM 服务读不到,与无人值守冲突)。
/// </summary>
internal static class AuthKeyStore
{
    // 存到 ProgramData(SYSTEM 与管理员都可读写),非用户目录
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "hsclient");
    private static readonly string FilePath = Path.Combine(Dir, "authkey.bin");

    // 固定 entropy(略微抬高门槛;非真正机密)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("hsclient-authkey-v1");

    public static void Save(string authKey)
    {
        Directory.CreateDirectory(Dir);
        byte[] cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(authKey), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(FilePath, cipher);
    }

    public static string Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return "";
            byte[] cipher = File.ReadAllBytes(FilePath);
            byte[] plain  = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }   // 解不开(机器换了/文件损坏)→ 当没存,等用户重填
    }
}
