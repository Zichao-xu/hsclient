; ============================================================================
;  hsclient 一键安装器(Inno Setup ≥6.3)
;  自有品牌 / 捆绑 tailscaled+tailscale(≥1.92.5)/ 注册并启动 Windows 服务 / TPM 免疫
;
;  ★ 落地核实结论(本机实测,见 SPEC §M3.3 评审 A1):
;    本版 tailscaled.exe 是【纯 flag 程序,无 install 子命令】
;    (`tailscaled.exe install` → "does not take non-flag arguments")。
;    因此服务注册不用 `tailscaled install`,改用 [Code] 段 `sc create`,
;    完全镜像官方 MSI 注册出的服务配置(binPath / start=auto / LocalSystem / 依赖项),
;    并做幂等(已存在则 sc config 更新),卸载用 `sc delete`(同理无 `tailscaled uninstall`)。
; ============================================================================

#define MyAppName "专网客户端"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "专网技术"
#define MyAppExeName "hsclient.exe"
#define TailscaledExe "tailscaled.exe"
#define TailscaleExe  "tailscale.exe"
#define SvcName "Tailscale"

[Setup]
AppId={{8016FB27-F77E-419B-9F8A-D421D9D31896}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=hsclient-Setup-{#MyAppVersion}
OutputDir=installer
SetupIconFile=branding\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "cn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; GUI(self-contained 单文件就一个 exe)
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 捆绑官方原生二进制(≥1.92.5,本机实测 v1.98.4)
Source: "bundle\{#TailscaledExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "bundle\{#TailscaleExe}";  DestDir: "{app}"; Flags: ignoreversion
; ★ wintun.dll 必须捆绑:本版 tailscaled 运行时不自释放,缺失则无法创建 TUN 虚拟网卡,
;   连接报 "A task was canceled"(本机实测,见缺陷修复记录)。捆绑到 {app},与 tailscaled.exe 同目录。
;   427552 字节 x64 build,源自 C:\Program Files\Tailscale\wintun.dll。
Source: "bundle\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; ★★ TPM 免疫:永久强制明文(SPEC §M3.4)★★
; ★ 必须 64 位注册表视图写(HKLM64 + Check: IsWin64):64 位 tailscaled 读非重定向路径;
;    落到 WOW6432Node 则免疫静默失效。[Registry] 在服务启动(ssPostInstall)之前执行,
;    所以服务首次启动即读到 EncryptState=0、state 以明文初始化。
Root: HKLM64; Subkey: "SOFTWARE\Policies\Tailscale"; ValueType: dword; ValueName: "EncryptState"; ValueData: 0; Flags: uninsdeletevalue; Check: IsWin64

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"
Name: "autostart";  Description: "登录时自动启动(后台托盘)"; GroupDescription: "启动选项:"

[Run]
; GUI 开机自启:计划任务(/RL HIGHEST + /SC ONLOGON),而非 HKCU Run
;  —— requireAdministrator 程序用 HKCU Run 登录时会弹 UAC 或被静默拦截(SPEC §M3.3 评审 C2)。
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Create /F /RL HIGHEST /SC ONLOGON /TN ""{#MyAppName}AutoStart"" /TR ""\""{app}\{#MyAppExeName}\"" /tray"""; \
    Flags: runhidden waituntilterminated; Tasks: autostart; StatusMsg: "正在配置开机自启..."
; 可选:装完直接拉起 GUI(静默安装时跳过)
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载顺序:先停服务 → 再删服务(顺序不能反,否则文件占用删不掉)
Filename: "{sys}\net.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopTS"
Filename: "{sys}\sc.exe";  Parameters: "delete {#SvcName}"; Flags: runhidden waituntilterminated; RunOnceId: "DelTS"
; 删掉开机自启计划任务
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""{#MyAppName}AutoStart"""; Flags: runhidden waituntilterminated; RunOnceId: "DelTask"

[Code]
const
  SvcName = '{#SvcName}';

// 同步执行 sc.exe,返回退出码(失败返回 -1)
function RunSc(const Params: string): Integer;
var
  Code: Integer;
begin
  if Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
end;

// 服务是否已存在(sc query 退出码 0 = 存在)
function ServiceExists(): Boolean;
begin
  Result := (RunSc('query ' + SvcName) = 0);
end;

function InitializeSetup(): Boolean;
begin
  // 重装幂等:先尝试停掉可能存在的旧服务(失败不阻断)
  RunSc('stop ' + SvcName);
  Result := True;
end;

procedure RegisterAndStartService();
var
  AppDir, BinPath, CreateParams: string;
begin
  AppDir  := ExpandConstant('{app}');
  BinPath := AppDir + '\{#TailscaledExe}';

  if ServiceExists() then
  begin
    // 已存在:更新 binPath 指向本安装目录的 tailscaled,并确保自动启动
    RunSc('config ' + SvcName + ' binPath= "' + BinPath + '" start= auto');
  end
  else
  begin
    // 全新注册:镜像官方 MSI 的服务配置(本机 sc qc 实测对齐)
    CreateParams :=
      'create ' + SvcName +
      ' binPath= "' + BinPath + '"' +
      ' start= auto' +
      ' DisplayName= "Tailscale"' +
      ' depend= Dnscache/iphlpsvc/netprofm/WinHttpAutoProxySvc' +
      ' obj= LocalSystem';
    RunSc(CreateParams);
    // 双保险:确保自动启动类型
    RunSc('config ' + SvcName + ' start= auto');
  end;

  // 启动服务(已 RUNNING 时 sc start 返回 1056,无害;下一轮 GUI 轮询复核)
  RunSc('start ' + SvcName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 文件已落地、[Registry] EncryptState=0 已写入 → 此时注册并首次启动服务,
  // 服务起来即读到明文策略(TPM 免疫生效时机正确,SPEC §M3.4 评审 D3)。
  if CurStep = ssPostInstall then
    RegisterAndStartService();
end;
