# hsclient

A minimal, branded Windows Tailscale client (WinForms GUI + bundled native `tailscaled`)
that connects to a self-hosted control server and runs unattended via a Windows service.

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
