# AgentHub: Claude Code instructions

The project rules live in `AGENTS.md`, shared with other coding agents:

@AGENTS.md

## Installing AgentHub from a fresh clone

When the user asks to install, set up or build AgentHub, follow these steps in order. Say what each step found. Ask before installing anything.

### 1. Check the prerequisites

Run these in PowerShell:

```powershell
[Environment]::OSVersion.Version          # 10.0.17763 (Windows 10 version 1809) or later
dotnet --list-sdks                        # needs a line starting with 8.
git --version
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue | Select-Object -First 1 pv   # WebView2 runtime version
```

Required. If one is missing, tell the user and install it with `winget` once they agree:

| Missing | Install |
|---|---|
| .NET 8 SDK (no `8.x` in `dotnet --list-sdks`) | `winget install --id Microsoft.DotNet.SDK.8 -e` |
| Git | `winget install --id Git.Git -e` |
| WebView2 runtime (no version shown) | `winget install --id Microsoft.EdgeWebView2Runtime -e` |

A newer SDK alone (9 or 10) is not enough. AgentHub targets `net8.0-windows`, so it needs the .NET 8 SDK or at least the .NET 8 runtime. A new install is not on `PATH` in the current shell. Open a new terminal, or call `"$env:ProgramFiles\dotnet\dotnet.exe"` directly. If `winget` is missing, send the user to https://dotnet.microsoft.com/download/dotnet/8.0.

Optional. Report which of these are on `PATH`, but do not install them unless the user asks: `gh` (GitHub CLI, for sign-in and the repository browser), `wt.exe` (Windows Terminal), `claude`, `codex`, `agy`.

### 2. Build and test

Run these from the repository root:

```powershell
dotnet restore .\AgentHub.sln
dotnet build .\AgentHub.sln -c Release --no-restore
dotnet test .\AgentHub.sln -c Release --no-build
```

The build must finish with 0 errors. Report any test that fails. Do not hide it or skip it.

### 3. Install the executable

Publish into a fixed install folder, outside the clone, so pulling new code never changes the app while it runs. `scripts\Deploy-Taskbar.ps1` does the publish, the copy and the check:

```powershell
.\scripts\Deploy-Taskbar.ps1 -InstallDir 'C:\Apps\AgentHub'
```

Use another folder if the user names one. Do not use `%APPDATA%`, `%LOCALAPPDATA%` or `%TEMP%`, because users cannot easily find files there. The script needs a clean working tree that contains `origin/main`, which a fresh clone has. It prints `DEPLOYED: <commit> is installed in '<folder>'` and starts the app.

### 4. Give the user a shortcut

Programs cannot pin to the taskbar themselves, so put a shortcut on the desktop and ask the user to pin it:

```powershell
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'AgentHub.lnk'))
$lnk.TargetPath = 'C:\Apps\AgentHub\AgentHub.exe'
$lnk.WorkingDirectory = 'C:\Apps\AgentHub'
$lnk.Save()
```

Tell the user to right-click `AgentHub.exe` or the shortcut and choose **Pin to taskbar**. Once it is pinned, `scripts\Deploy-Taskbar.ps1` finds the install folder from the pin, so `-InstallDir` is no longer needed.

### 5. Report

Give the user:
- the .NET SDK version used
- the install folder
- the installed commit (from the `DEPLOYED:` line)
- which optional tools are missing

Then point them to **First launch** in `README.md`.

### Updating later

After a `git pull`, run `.\scripts\Deploy-Taskbar.ps1 -Check`. If it says `OUT OF DATE`, ask the user whether AgentHub may close, then run `.\scripts\Deploy-Taskbar.ps1 -CloseRunning`. Without a pinned shortcut, pass the same `-InstallDir` as before.
