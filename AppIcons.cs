using System.Drawing;
using System.Reflection;

namespace hsclient;

/// <summary>
/// 加载嵌入的托盘状态图标(online 绿 / offline 灰)。
/// 用嵌入资源而非 Properties.Resources,避免 resx/designer 维护;单文件发布下同样可用。
/// 多帧 .ico(16/20/24/32/48)在高 DPI 下不模糊。
/// </summary>
internal static class AppIcons
{
    private static Icon? _online;
    private static Icon? _offline;

    public static Icon Online  => _online  ??= Load("online.ico");
    public static Icon Offline => _offline ??= Load("offline.ico");

    private static Icon Load(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // 嵌入名形如 hsclient.branding.online.ico —— 用后缀匹配,稳健不怕命名空间变化
        string? resName = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        if (resName != null)
        {
            using var s = asm.GetManifestResourceStream(resName);
            if (s != null) return new Icon(s);
        }
        // 兜底:实在找不到就用系统图标,绝不让托盘初始化崩
        return SystemIcons.Application;
    }
}
