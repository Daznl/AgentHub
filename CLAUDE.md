# AgentHub: Claude Code instructions

The project rules live in `AGENTS.md`, shared with other coding agents:

@AGENTS.md

## Installing AgentHub from a fresh clone

When the user asks to install, set up or build AgentHub, follow these steps in order. Say what each step found. Ask before installing anything.

### 1. Check the prerequisites

Run `.\bootstrap.cmd -CheckOnly` from the repository root. It reports every required and optional tool and changes nothing. If Git is missing, the clone cannot exist yet; if PowerShell cannot run the script, check by hand:

```powershell
[Environment]::OSVersion.Version          # 10.0.17763 (Windows 10 version 1809) or later
dotnet --list-sdks                        # any SDK 8.x or later builds it
dotnet --list-runtimes                    # running needs a line "Microsoft.WindowsDesktop.App 8."
git --version
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue | Select-Object -First 1 pv   # WebView2 runtime version
```

Required. If one is missing, tell the user and install it with `winget` once they agree:

| Missing | Install |
|---|---|
| No .NET SDK, or none at 8 or later (`dotnet` missing, or `dotnet --list-sdks` empty or only older versions) | `winget install --id Microsoft.DotNet.SDK.8 -e`. This also brings the .NET 8 Desktop Runtime. |
| A newer SDK (9, 10…) but no `Microsoft.WindowsDesktop.App 8.` runtime | `winget install --id Microsoft.DotNet.DesktopRuntime.8 -e` |
| Git | `winget install --id Git.Git -e` |
| WebView2 runtime (no version shown) | `winget install --id Microsoft.EdgeWebView2Runtime -e` |

AgentHub targets `net8.0-windows`. A newer SDK builds it, but the app and the tests run only on the .NET 8 Desktop Runtime, because .NET does not move to a newer major version by itself. A new install is not on `PATH` in the current shell. Open a new terminal, or call `"$env:ProgramFiles\dotnet\dotnet.exe"` directly. If `winget` is missing or cannot reach its source (some company networks block it), send the user to the download pages instead: https://dotnet.microsoft.com/download/dotnet/8.0, https://git-scm.com/download/win, https://developer.microsoft.com/microsoft-edge/webview2/.

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
- the .NET SDK and Desktop Runtime versions used
- the install folder
- the installed commit (from the `DEPLOYED:` line)
- which optional tools are missing

Then point them to **First launch** in `README.md`.

### Updating later

After a `git pull`, run `.\scripts\Deploy-Taskbar.cmd -Check`. If it says `OUT OF DATE`, ask the user whether AgentHub may close, then run `.\scripts\Deploy-Taskbar.cmd -CloseRunning`. Without a pinned shortcut, pass the same `-InstallDir` as before.

### When the user asks to rebuild or install the app

"Rebuild the executable", "put it on my taskbar", "install it", "deploy it": that is the go. Do this and nothing else:

1. Commit the work (a branch is fine; the script needs `origin/main` merged in, so `git merge origin/main` first if behind).
2. `.\scripts\Deploy-Taskbar.cmd -CloseRunning`. It finds the pinned shortcut whatever Windows named it (`AgentHub (2).lnk` and so on), builds, closes the app, installs and relaunches. Running sessions survive.
3. Report the `DEPLOYED:` commit and folder.

Do not ask again whether the app may close, do not create a desktop shortcut, and do not tell the user to pin it when a pin already exists. Use the `.cmd`, not the `.ps1`: the execution policy on this computer blocks PowerShell scripts.
