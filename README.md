# hsclient

A minimal, branded Windows Tailscale client (WinForms GUI + bundled native `tailscaled`)
that connects to a self-hosted control server and runs unattended via a Windows service.

## Disconnect and exit (1.0.1)

- The main button and tray **断开** disconnect the tunnel and leave the service available.
- Closing the window with **X** still minimizes to the tray and keeps the connection.
- Tray **退出并断开** cancels pending connections/recovery, disconnects the tunnel, stops
  the Windows service, verifies that it stopped, and then closes the application.
- If stopping the service fails, the application stays open with a readable warning so
  the user can retry. A failed stop is never reported as a successful exit.
- Open the client again and click **连接** to restart the service and reconnect using the
  saved server address and masked access key.

Network performance is a separate acceptance item: this change does not repair an
unreachable DERP relay or establish that remote SMB/RDP performance has improved.

The existing installer and official Windows client share the `Tailscale` service name.
The installer redirects that service to the bundled daemon. Use this client's tray exit
for the new shutdown behavior; running both frontends can give the service conflicting
connection requests. This change does not redesign service/profile ownership.

## Regression checks

Run on Windows with .NET 8 and administrator rights:

```powershell
dotnet run --project tests/hsclient.Tests.csproj -c Release
```

The checks cover manual disconnect, delayed/in-flight recovery, exit during connection,
failed stops and retry, restart after exit, and the full WinForms/tray flow. They use
isolated settings and a fake VPN connection. One integration check creates a temporary
Windows service, tests native service start/stop, and deletes it afterward. The installed
Tailscale service, live VPN connection, and saved credentials are not modified.
GitHub Actions runs the same Windows checks on pushes and pull requests.

## Bundled binaries (required before building the installer)

The `bundle/` folder is **intentionally excluded from source control** (see `.gitignore`).
Before building the Inno Setup installer (`hsclient.iss`), you must place the following
**official, unmodified** Tailscale binaries into `bundle/`:

| File             | Notes                                                              |
| ---------------- | ----------------------------------------------------------------- |
| `tailscale.exe`  | Official Tailscale CLI, **version 1.92.5 or newer**.              |
| `tailscaled.exe` | Official Tailscale daemon, **version 1.92.5 or newer**.           |
| `wintun.dll`     | x64 WinTun driver DLL. This `tailscaled` build does **not** self-extract it at runtime — if it is missing, the TUN adapter cannot be created and connecting fails with `"A task was canceled"`. Source it from an official Tailscale install (e.g. `C:\Program Files\Tailscale\wintun.dll`). |

Download official binaries from <https://tailscale.com/download/windows>. Do not commit
these files — they are large vendor artifacts, not project source.

## TPM immunity (`EncryptState=0`)

The installer (`hsclient.iss`) pre-sets the registry policy
`HKLM64\SOFTWARE\Policies\Tailscale\EncryptState = 0` (DWORD) **before** the `Tailscale`
service first starts. This makes the daemon initialize its `state` file in plaintext rather
than sealing it to the machine's TPM, so the state survives TPM changes / hardware moves
(TPM immunity) and an administrator can read/relocate it. The 64-bit registry view
(`HKLM64`) is required so the 64-bit `tailscaled` reads the non-redirected path.

## Build

1. Drop the three binaries above into `bundle/`.
2. Build the .NET project: `dotnet publish -c Release` (output → `publish/`).
3. Compile `hsclient.iss` with Inno Setup to produce `installer/hsclient-Setup-<ver>.exe`.
